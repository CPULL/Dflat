/*
 * Gfx — the C core of the Dflat gfx library (see gfx.h). Vulkan 1.1 + GLFW 3.3.
 *
 * The shaders are embedded: gfx_vert.h and gfx_frag.h are made from shader.vert and shader.frag by make_shaders.ps1 (glslangValidator of
 * the Vulkan SDK). Run it again after a change of the GLSL.
 *
 * Layout: the context owns the Vulkan instance, the device and every object; the objects live in pools of slots (one pool per kind) and are
 * reached through handles (generation, kind, slot). The device is created with the context, so textures can be made before the window; the
 * window owns the surface, the swapchain, the pipeline and the per-frame resources.
 */

#define GLFW_INCLUDE_VULKAN
#include <GLFW/glfw3.h>

#include "gfx.h"
#include "gfx_vert.h"   /* const uint32_t gfx_vert_spv[] */
#include "gfx_frag.h"   /* const uint32_t gfx_frag_spv[] */

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define GFX_FRAMES          2       /* frames in flight */
#define GFX_FIRST_QUADS     1024    /* the vertex buffers start with room for this many quads and double when needed */
#define GFX_MAX_TEXTURES    4096    /* descriptor sets: one per texture */

enum { KIND_WINDOW = 1, KIND_TEXTURE, KIND_SPRITE, KIND_FONT, KIND_LABEL };

/* ------------------------------------------------------------------ */
/*  Pools of slots and handles                                        */
/* ------------------------------------------------------------------ */

typedef struct {
    uint32_t generation;   /* the generation of the object in the slot (or of the next one, when free) */
    bool     used;
} Slot;

typedef struct {
    char*    data;
    uint32_t count;        /* slots in use or released (never shrinks) */
    uint32_t capacity;
    size_t   size;         /* bytes per item; every item starts with a Slot */
} Pool;

static Slot* pool_at(Pool* p, uint32_t i) { return (Slot*)(p->data + (size_t)i * p->size); }

/* a free slot (a released one first), its Slot already marked used; UINT32_MAX when out of memory */
static uint32_t pool_alloc(Pool* p) {
    for (uint32_t i = 0; i < p->count; i++) {
        Slot* s = pool_at(p, i);
        if (!s->used) {
            uint32_t generation = s->generation;
            memset(s, 0, p->size);
            s->generation = generation;
            s->used = true;
            return i;
        }
    }
    if (p->count == (1u << 24)) {
        return UINT32_MAX;
    }
    if (p->count == p->capacity) {
        uint32_t capacity = p->capacity ? p->capacity * 2 : 16;
        char* data = (char*)realloc(p->data, (size_t)capacity * p->size);
        if (!data) {
            return UINT32_MAX;
        }
        p->data = data;
        p->capacity = capacity;
    }
    Slot* s = pool_at(p, p->count);
    memset(s, 0, p->size);
    s->generation = 1;
    s->used = true;
    return p->count++;
}

static void pool_free_slot(Slot* s) {
    s->used = false;
    s->generation = s->generation == UINT32_MAX ? 1 : s->generation + 1;   /* the old handles become stale */
}

static uint64_t make_handle(uint32_t generation, int kind, uint32_t index) {
    return ((uint64_t)generation << 32) | ((uint64_t)(uint32_t)kind << 24) | (uint64_t)index;
}

/* ------------------------------------------------------------------ */
/*  Objects                                                           */
/* ------------------------------------------------------------------ */

typedef struct {
    Slot            slot;
    VkImage         image;
    VkDeviceMemory  memory;
    VkImageView     view;
    VkSampler       sampler;
    VkDescriptorSet set;
    int32_t         width, height, filter;
} Texture;

typedef struct {
    Slot     slot;
    uint64_t texture;
    float    pos_x, pos_y, offset_x, offset_y, scale_x, scale_y, rotation;
    int32_t  order;
    float    tl_x, tl_y, br_x, br_y;
    bool     visible;
    uint64_t serial;       /* creation order: equal orders are drawn in it */
} Sprite;

typedef struct {
    Slot     slot;
    uint64_t texture;
    int32_t  glyph_width, glyph_height;
} Font;

typedef struct {
    Slot     slot;
    uint64_t font;
    char*    text;
    float    pos_x, pos_y, offset_x, offset_y, scale_x, scale_y, rotation;
    int32_t  order;
    bool     visible;
    float    color[4];
    uint64_t serial;
} Label;

typedef struct {
    VkBuffer       buffer;
    VkDeviceMemory memory;
    void*          mapped;
} Buffer;

typedef struct {
    float x, y, u, v, r, g, b, a;
} Vertex;

typedef struct {
    bool            exists;
    uint32_t        generation;
    GLFWwindow*     glfw;
    bool            resized;
    int32_t         world_width, world_height;   /* the size given at creation: what the scaling modes scale */
    int32_t         resize_mode;
    bool            integer_scale;

    VkSurfaceKHR    surface;
    VkSwapchainKHR  swapchain;
    VkFormat        format;
    VkColorSpaceKHR color_space;
    VkExtent2D      extent;
    uint32_t        image_count;
    VkImage*        images;
    VkImageView*    views;
    VkFramebuffer*  framebuffers;
    VkSemaphore*    render_finished;             /* one per swapchain image */
    VkRenderPass    render_pass;
    VkPipeline      pipeline;

    VkCommandBuffer cmd[GFX_FRAMES];
    VkSemaphore     image_available[GFX_FRAMES];
    VkFence         in_flight[GFX_FRAMES];
    Buffer          vertices[GFX_FRAMES];
    Buffer          indices;
    uint32_t        quad_capacity;
    uint32_t        frame;
} Window;

typedef struct {
    VkInstance            instance;
    VkPhysicalDevice      physical;
    VkDevice              device;
    uint32_t              family;                /* graphics and present */
    VkQueue               queue;
    VkCommandPool         command_pool;
    VkDescriptorSetLayout set_layout;
    VkDescriptorPool      descriptor_pool;
    VkPipelineLayout      pipeline_layout;

    Window   window;
    Pool     textures, sprites, fonts, labels;
    uint64_t serial;
    float    clear[4];
} Context;

/* ------------------------------------------------------------------ */
/*  GLFW: initialized once for every context and query                */
/* ------------------------------------------------------------------ */

static int glfw_users = 0;

static bool glfw_acquire(void) {
    if (glfw_users == 0 && !glfwInit()) {
        fprintf(stderr, "[gfx] glfwInit failed\n");
        return false;
    }
    glfw_users++;
    return true;
}

static void glfw_release(void) {
    if (glfw_users > 0 && --glfw_users == 0) {
        glfwTerminate();
    }
}

/* ------------------------------------------------------------------ */
/*  Lookup                                                            */
/* ------------------------------------------------------------------ */

static Pool* pool_of(Context* c, int kind) {
    switch (kind) {
        case KIND_TEXTURE: return &c->textures;
        case KIND_SPRITE:  return &c->sprites;
        case KIND_FONT:    return &c->fonts;
        case KIND_LABEL:   return &c->labels;
        default:           return NULL;
    }
}

/* the object of a handle of that kind, or NULL when the handle is stale or of another kind */
static void* lookup(Context* c, uint64_t handle, int kind) {
    if (!c || ((handle >> 24) & 0xFF) != (uint64_t)kind) {
        return NULL;
    }
    uint32_t generation = (uint32_t)(handle >> 32);
    uint32_t index = (uint32_t)(handle & 0xFFFFFF);
    Pool* p = pool_of(c, kind);
    if (!p || index >= p->count) {
        return NULL;
    }
    Slot* s = pool_at(p, index);
    return s->used && s->generation == generation ? s : NULL;
}

static Window* lookup_window(Context* c, uint64_t handle) {
    if (!c || !c->window.exists || handle != make_handle(c->window.generation, KIND_WINDOW, 0)) {
        return NULL;
    }
    return &c->window;
}

/* ------------------------------------------------------------------ */
/*  Vulkan helpers                                                    */
/* ------------------------------------------------------------------ */

static uint32_t find_memory_type(Context* c, uint32_t bits, VkMemoryPropertyFlags props) {
    VkPhysicalDeviceMemoryProperties mem;
    vkGetPhysicalDeviceMemoryProperties(c->physical, &mem);
    for (uint32_t i = 0; i < mem.memoryTypeCount; i++) {
        if ((bits & (1u << i)) && (mem.memoryTypes[i].propertyFlags & props) == props) {
            return i;
        }
    }
    return UINT32_MAX;
}

static bool buffer_create(Context* c, VkDeviceSize size, VkBufferUsageFlags usage, Buffer* out) {
    memset(out, 0, sizeof *out);
    VkBufferCreateInfo bi = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    bi.size = size;
    bi.usage = usage;
    bi.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (vkCreateBuffer(c->device, &bi, NULL, &out->buffer) != VK_SUCCESS) {
        return false;
    }
    VkMemoryRequirements req;
    vkGetBufferMemoryRequirements(c->device, out->buffer, &req);
    VkMemoryAllocateInfo ai = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = find_memory_type(c, req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (ai.memoryTypeIndex == UINT32_MAX || vkAllocateMemory(c->device, &ai, NULL, &out->memory) != VK_SUCCESS) {
        vkDestroyBuffer(c->device, out->buffer, NULL);
        out->buffer = VK_NULL_HANDLE;
        return false;
    }
    vkBindBufferMemory(c->device, out->buffer, out->memory, 0);
    vkMapMemory(c->device, out->memory, 0, size, 0, &out->mapped);
    return true;
}

static void buffer_destroy(Context* c, Buffer* b) {
    if (b->mapped) vkUnmapMemory(c->device, b->memory);
    if (b->buffer) vkDestroyBuffer(c->device, b->buffer, NULL);
    if (b->memory) vkFreeMemory(c->device, b->memory, NULL);
    memset(b, 0, sizeof *b);
}

static VkCommandBuffer one_time_begin(Context* c) {
    VkCommandBufferAllocateInfo ai = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    ai.commandPool = c->command_pool;
    ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = 1;
    VkCommandBuffer cmd;
    vkAllocateCommandBuffers(c->device, &ai, &cmd);
    VkCommandBufferBeginInfo bi = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    vkBeginCommandBuffer(cmd, &bi);
    return cmd;
}

static void one_time_end(Context* c, VkCommandBuffer cmd) {
    vkEndCommandBuffer(cmd);
    VkSubmitInfo si = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    vkQueueSubmit(c->queue, 1, &si, VK_NULL_HANDLE);
    vkQueueWaitIdle(c->queue);
    vkFreeCommandBuffers(c->device, c->command_pool, 1, &cmd);
}

static void image_barrier(VkCommandBuffer cmd, VkImage image, VkImageLayout from, VkImageLayout to,
                          VkAccessFlags src_access, VkAccessFlags dst_access, VkPipelineStageFlags src_stage, VkPipelineStageFlags dst_stage) {
    VkImageMemoryBarrier b = { VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
    b.oldLayout = from;
    b.newLayout = to;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = image;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1;
    b.subresourceRange.layerCount = 1;
    b.srcAccessMask = src_access;
    b.dstAccessMask = dst_access;
    vkCmdPipelineBarrier(cmd, src_stage, dst_stage, 0, 0, NULL, 0, NULL, 1, &b);
}

/* copies RGBA8 pixels into an image; first: the image has no content yet (layout undefined) */
static bool upload_pixels(Context* c, VkImage image, int32_t width, int32_t height, const uint8_t* rgba, bool first) {
    VkDeviceSize size = (VkDeviceSize)width * (VkDeviceSize)height * 4;
    Buffer staging;
    if (!buffer_create(c, size, VK_BUFFER_USAGE_TRANSFER_SRC_BIT, &staging)) {
        return false;
    }
    memcpy(staging.mapped, rgba, (size_t)size);
    VkCommandBuffer cmd = one_time_begin(c);
    image_barrier(cmd, image, first ? VK_IMAGE_LAYOUT_UNDEFINED : VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                  first ? 0 : VK_ACCESS_SHADER_READ_BIT, VK_ACCESS_TRANSFER_WRITE_BIT,
                  first ? VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT : VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkBufferImageCopy region = { 0 };
    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.imageSubresource.layerCount = 1;
    region.imageExtent.width = (uint32_t)width;
    region.imageExtent.height = (uint32_t)height;
    region.imageExtent.depth = 1;
    vkCmdCopyBufferToImage(cmd, staging.buffer, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    image_barrier(cmd, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                  VK_ACCESS_TRANSFER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
    one_time_end(c, cmd);
    buffer_destroy(c, &staging);
    return true;
}

static VkSampler sampler_create(Context* c, int32_t filter) {
    VkSamplerCreateInfo si = { VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO };
    si.magFilter = si.minFilter = filter == GFX_FILTER_LINEAR ? VK_FILTER_LINEAR : VK_FILTER_NEAREST;
    si.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    si.addressModeU = si.addressModeV = si.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    si.maxAnisotropy = 1.0f;
    si.borderColor = VK_BORDER_COLOR_INT_OPAQUE_BLACK;
    VkSampler sampler = VK_NULL_HANDLE;
    vkCreateSampler(c->device, &si, NULL, &sampler);
    return sampler;
}

static void descriptor_write(Context* c, Texture* t) {
    VkDescriptorImageInfo info = { t->sampler, t->view, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL };
    VkWriteDescriptorSet w = { VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET };
    w.dstSet = t->set;
    w.descriptorCount = 1;
    w.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    w.pImageInfo = &info;
    vkUpdateDescriptorSets(c->device, 1, &w, 0, NULL);
}

static void texture_destroy(Context* c, Texture* t) {
    if (t->set) vkFreeDescriptorSets(c->device, c->descriptor_pool, 1, &t->set);
    if (t->sampler) vkDestroySampler(c->device, t->sampler, NULL);
    if (t->view) vkDestroyImageView(c->device, t->view, NULL);
    if (t->image) vkDestroyImage(c->device, t->image, NULL);
    if (t->memory) vkFreeMemory(c->device, t->memory, NULL);
    t->set = VK_NULL_HANDLE;
    t->sampler = VK_NULL_HANDLE;
    t->view = VK_NULL_HANDLE;
    t->image = VK_NULL_HANDLE;
    t->memory = VK_NULL_HANDLE;
}

/* ------------------------------------------------------------------ */
/*  Instance and device                                               */
/* ------------------------------------------------------------------ */

static VkInstance instance_create(void) {
    VkApplicationInfo app = { VK_STRUCTURE_TYPE_APPLICATION_INFO };
    app.pApplicationName = "Dflat gfx";
    app.pEngineName = "Dflat gfx";
    app.apiVersion = VK_API_VERSION_1_1;
    uint32_t count = 0;
    const char** extensions = glfwGetRequiredInstanceExtensions(&count);
    if (!extensions) {
        return VK_NULL_HANDLE;
    }
    VkInstanceCreateInfo ci = { VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
    ci.pApplicationInfo = &app;
    ci.enabledExtensionCount = count;
    ci.ppEnabledExtensionNames = extensions;
    VkInstance instance = VK_NULL_HANDLE;
    return vkCreateInstance(&ci, NULL, &instance) == VK_SUCCESS ? instance : VK_NULL_HANDLE;
}

static bool has_swapchain_extension(VkPhysicalDevice device) {
    uint32_t count = 0;
    vkEnumerateDeviceExtensionProperties(device, NULL, &count, NULL);
    VkExtensionProperties* props = (VkExtensionProperties*)malloc(count * sizeof *props);
    if (!props) {
        return false;
    }
    vkEnumerateDeviceExtensionProperties(device, NULL, &count, props);
    bool found = false;
    for (uint32_t i = 0; i < count && !found; i++) {
        found = strcmp(props[i].extensionName, VK_KHR_SWAPCHAIN_EXTENSION_NAME) == 0;
    }
    free(props);
    return found;
}

/* a GPU with one queue family for graphics and presentation, and swapchains; a discrete GPU first */
static bool device_pick(VkInstance instance, VkPhysicalDevice* out_device, uint32_t* out_family) {
    uint32_t count = 0;
    vkEnumeratePhysicalDevices(instance, &count, NULL);
    if (count == 0) {
        return false;
    }
    VkPhysicalDevice* devices = (VkPhysicalDevice*)malloc(count * sizeof *devices);
    if (!devices) {
        return false;
    }
    vkEnumeratePhysicalDevices(instance, &count, devices);
    bool found = false;
    for (int pass = 0; pass < 2 && !found; pass++) {
        for (uint32_t i = 0; i < count && !found; i++) {
            VkPhysicalDeviceProperties props;
            vkGetPhysicalDeviceProperties(devices[i], &props);
            if (pass == 0 && props.deviceType != VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU) {
                continue;
            }
            if (!has_swapchain_extension(devices[i])) {
                continue;
            }
            uint32_t families = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(devices[i], &families, NULL);
            VkQueueFamilyProperties* fp = (VkQueueFamilyProperties*)malloc(families * sizeof *fp);
            if (!fp) {
                continue;
            }
            vkGetPhysicalDeviceQueueFamilyProperties(devices[i], &families, fp);
            for (uint32_t q = 0; q < families && !found; q++) {
                if ((fp[q].queueFlags & VK_QUEUE_GRAPHICS_BIT) && glfwGetPhysicalDevicePresentationSupport(instance, devices[i], q)) {
                    *out_device = devices[i];
                    *out_family = q;
                    found = true;
                }
            }
            free(fp);
        }
    }
    free(devices);
    return found;
}

/* ------------------------------------------------------------------ */
/*  The system                                                        */
/* ------------------------------------------------------------------ */

bool gfx_is_available(void) {
    if (!glfw_acquire()) {
        return false;
    }
    bool ok = false;
    if (glfwVulkanSupported()) {
        VkInstance instance = instance_create();
        if (instance) {
            VkPhysicalDevice device;
            uint32_t family;
            ok = device_pick(instance, &device, &family);
            vkDestroyInstance(instance, NULL);
        }
    }
    glfw_release();
    return ok;
}

static GLFWmonitor* monitor_at(int32_t index) {
    int count = 0;
    GLFWmonitor** monitors = glfwGetMonitors(&count);
    return monitors && index >= 0 && index < count ? monitors[index] : NULL;
}

int32_t gfx_display_count(void) {
    if (!glfw_acquire()) {
        return 0;
    }
    int count = 0;
    glfwGetMonitors(&count);
    glfw_release();
    return (int32_t)count;
}

/* what: 0 x, 1 y, 2 width, 3 height, 4 primary */
static int32_t display_value(int32_t index, int what) {
    if (!glfw_acquire()) {
        return 0;
    }
    int32_t value = 0;
    GLFWmonitor* m = monitor_at(index);
    if (m) {
        int x = 0, y = 0;
        glfwGetMonitorPos(m, &x, &y);
        const GLFWvidmode* mode = glfwGetVideoMode(m);
        switch (what) {
            case 0: value = x; break;
            case 1: value = y; break;
            case 2: value = mode ? mode->width : 0; break;
            case 3: value = mode ? mode->height : 0; break;
            case 4: value = m == glfwGetPrimaryMonitor(); break;
        }
    }
    glfw_release();
    return value;
}

int32_t gfx_display_x(int32_t index)      { return display_value(index, 0); }
int32_t gfx_display_y(int32_t index)      { return display_value(index, 1); }
int32_t gfx_display_width(int32_t index)  { return display_value(index, 2); }
int32_t gfx_display_height(int32_t index) { return display_value(index, 3); }
bool    gfx_display_primary(int32_t index) { return display_value(index, 4) != 0; }

int32_t gfx_display_name(int32_t index, uint8_t* buffer, size_t size) {
    if (!glfw_acquire()) {
        return 0;
    }
    GLFWmonitor* m = monitor_at(index);
    const char* name = m ? glfwGetMonitorName(m) : NULL;
    size_t length = name ? strlen(name) : 0;
    if (length > size) {
        length = size;
    }
    if (length > 0) {
        memcpy(buffer, name, length);
    }
    glfw_release();
    return (int32_t)length;
}

/* ------------------------------------------------------------------ */
/*  The context                                                       */
/* ------------------------------------------------------------------ */

static void window_destroy(Context* c);

void* gfx_context_create(void) {
    if (!glfw_acquire()) {
        return NULL;
    }
    Context* c = (Context*)calloc(1, sizeof(Context));
    if (!c) {
        glfw_release();
        return NULL;
    }
    c->textures.size = sizeof(Texture);
    c->sprites.size = sizeof(Sprite);
    c->fonts.size = sizeof(Font);
    c->labels.size = sizeof(Label);
    c->clear[3] = 1.0f;
    c->window.generation = 1;

    if (!glfwVulkanSupported() || !(c->instance = instance_create()) || !device_pick(c->instance, &c->physical, &c->family)) {
        gfx_context_destroy(c);
        return NULL;
    }
    float priority = 1.0f;
    VkDeviceQueueCreateInfo qi = { VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO };
    qi.queueFamilyIndex = c->family;
    qi.queueCount = 1;
    qi.pQueuePriorities = &priority;
    const char* extensions[] = { VK_KHR_SWAPCHAIN_EXTENSION_NAME };
    VkDeviceCreateInfo di = { VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO };
    di.queueCreateInfoCount = 1;
    di.pQueueCreateInfos = &qi;
    di.enabledExtensionCount = 1;
    di.ppEnabledExtensionNames = extensions;
    if (vkCreateDevice(c->physical, &di, NULL, &c->device) != VK_SUCCESS) {
        c->device = VK_NULL_HANDLE;
        gfx_context_destroy(c);
        return NULL;
    }
    vkGetDeviceQueue(c->device, c->family, 0, &c->queue);

    VkCommandPoolCreateInfo pi = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    pi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pi.queueFamilyIndex = c->family;
    VkDescriptorSetLayoutBinding binding = { 0 };
    binding.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    binding.descriptorCount = 1;
    binding.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    VkDescriptorSetLayoutCreateInfo li = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO };
    li.bindingCount = 1;
    li.pBindings = &binding;
    VkDescriptorPoolSize size = { VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, GFX_MAX_TEXTURES };
    VkDescriptorPoolCreateInfo dpi = { VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO };
    dpi.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;   /* a released texture gives its set back */
    dpi.maxSets = GFX_MAX_TEXTURES;
    dpi.poolSizeCount = 1;
    dpi.pPoolSizes = &size;
    VkPushConstantRange push = { VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(float) * 16 };
    VkPipelineLayoutCreateInfo pli = { VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO };
    pli.setLayoutCount = 1;
    pli.pushConstantRangeCount = 1;
    pli.pPushConstantRanges = &push;
    if (vkCreateCommandPool(c->device, &pi, NULL, &c->command_pool) != VK_SUCCESS
        || vkCreateDescriptorSetLayout(c->device, &li, NULL, &c->set_layout) != VK_SUCCESS
        || vkCreateDescriptorPool(c->device, &dpi, NULL, &c->descriptor_pool) != VK_SUCCESS
        || (pli.pSetLayouts = &c->set_layout, vkCreatePipelineLayout(c->device, &pli, NULL, &c->pipeline_layout)) != VK_SUCCESS) {
        gfx_context_destroy(c);
        return NULL;
    }
    return c;
}

void gfx_context_destroy(void* ctx) {
    Context* c = (Context*)ctx;
    if (!c) {
        return;
    }
    if (c->device) {
        vkDeviceWaitIdle(c->device);
        window_destroy(c);
        for (uint32_t i = 0; i < c->textures.count; i++) {
            Texture* t = (Texture*)pool_at(&c->textures, i);
            if (t->slot.used) {
                texture_destroy(c, t);
            }
        }
        if (c->pipeline_layout) vkDestroyPipelineLayout(c->device, c->pipeline_layout, NULL);
        if (c->descriptor_pool) vkDestroyDescriptorPool(c->device, c->descriptor_pool, NULL);
        if (c->set_layout) vkDestroyDescriptorSetLayout(c->device, c->set_layout, NULL);
        if (c->command_pool) vkDestroyCommandPool(c->device, c->command_pool, NULL);
        vkDestroyDevice(c->device, NULL);
    }
    if (c->instance) {
        vkDestroyInstance(c->instance, NULL);
    }
    for (uint32_t i = 0; i < c->labels.count; i++) {
        Label* l = (Label*)pool_at(&c->labels, i);
        if (l->slot.used) {
            free(l->text);
        }
    }
    free(c->textures.data);
    free(c->sprites.data);
    free(c->fonts.data);
    free(c->labels.data);
    free(c);
    glfw_release();
}

bool gfx_valid(void* ctx, uint64_t handle) {
    Context* c = (Context*)ctx;
    int kind = (int)((handle >> 24) & 0xFF);
    return kind == KIND_WINDOW ? lookup_window(c, handle) != NULL : lookup(c, handle, kind) != NULL;
}

void gfx_release(void* ctx, uint64_t handle) {
    Context* c = (Context*)ctx;
    int kind = (int)((handle >> 24) & 0xFF);
    if (kind == KIND_WINDOW) {
        if (lookup_window(c, handle)) {
            vkDeviceWaitIdle(c->device);
            window_destroy(c);
        }
        return;
    }
    Slot* s = (Slot*)lookup(c, handle, kind);
    if (!s) {
        return;
    }
    if (kind == KIND_TEXTURE) {
        vkDeviceWaitIdle(c->device);   /* the GPU may still read it: freed once it has finished */
        texture_destroy(c, (Texture*)s);
    } else if (kind == KIND_LABEL) {
        free(((Label*)s)->text);
        ((Label*)s)->text = NULL;
    }
    pool_free_slot(s);
}

void gfx_set_clear_color(void* ctx, float r, float g, float b, float a) {
    Context* c = (Context*)ctx;
    if (c) {
        c->clear[0] = r;
        c->clear[1] = g;
        c->clear[2] = b;
        c->clear[3] = a;
    }
}

/* ------------------------------------------------------------------ */
/*  The window                                                        */
/* ------------------------------------------------------------------ */

static void on_framebuffer_resize(GLFWwindow* glfw, int width, int height) {
    (void)width;
    (void)height;
    Context* c = (Context*)glfwGetWindowUserPointer(glfw);
    if (c) {
        c->window.resized = true;
    }
}

static void swapchain_destroy(Context* c) {
    Window* w = &c->window;
    for (uint32_t i = 0; i < w->image_count; i++) {
        if (w->framebuffers && w->framebuffers[i]) vkDestroyFramebuffer(c->device, w->framebuffers[i], NULL);
        if (w->views && w->views[i]) vkDestroyImageView(c->device, w->views[i], NULL);
        if (w->render_finished && w->render_finished[i]) vkDestroySemaphore(c->device, w->render_finished[i], NULL);
    }
    free(w->framebuffers);
    free(w->views);
    free(w->images);
    free(w->render_finished);
    w->framebuffers = NULL;
    w->views = NULL;
    w->images = NULL;
    w->render_finished = NULL;
    w->image_count = 0;
    if (w->swapchain) {
        vkDestroySwapchainKHR(c->device, w->swapchain, NULL);
        w->swapchain = VK_NULL_HANDLE;
    }
}

/* the swapchain and its images; the first time (format_known false) it also chooses the format the render pass is made for */
static bool swapchain_create(Context* c, bool format_known) {
    Window* w = &c->window;
    VkSurfaceCapabilitiesKHR caps;
    vkGetPhysicalDeviceSurfaceCapabilitiesKHR(c->physical, w->surface, &caps);
    if (!format_known) {
        uint32_t count = 0;
        vkGetPhysicalDeviceSurfaceFormatsKHR(c->physical, w->surface, &count, NULL);
        VkSurfaceFormatKHR* formats = (VkSurfaceFormatKHR*)malloc(count * sizeof *formats);
        if (!formats || count == 0) {
            free(formats);
            return false;
        }
        vkGetPhysicalDeviceSurfaceFormatsKHR(c->physical, w->surface, &count, formats);
        w->format = formats[0].format;
        w->color_space = formats[0].colorSpace;
        for (uint32_t i = 0; i < count; i++) {
            if (formats[i].format == VK_FORMAT_B8G8R8A8_SRGB && formats[i].colorSpace == VK_COLOR_SPACE_SRGB_NONLINEAR_KHR) {
                w->format = formats[i].format;
                w->color_space = formats[i].colorSpace;
                break;
            }
        }
        free(formats);
    }
    if (caps.currentExtent.width != UINT32_MAX) {
        w->extent = caps.currentExtent;
    } else {
        int fw = 0, fh = 0;
        glfwGetFramebufferSize(w->glfw, &fw, &fh);
        w->extent.width = (uint32_t)fw;
        w->extent.height = (uint32_t)fh;
        if (w->extent.width < caps.minImageExtent.width) w->extent.width = caps.minImageExtent.width;
        if (w->extent.height < caps.minImageExtent.height) w->extent.height = caps.minImageExtent.height;
        if (w->extent.width > caps.maxImageExtent.width) w->extent.width = caps.maxImageExtent.width;
        if (w->extent.height > caps.maxImageExtent.height) w->extent.height = caps.maxImageExtent.height;
    }
    uint32_t images = caps.minImageCount + 1;
    if (caps.maxImageCount > 0 && images > caps.maxImageCount) {
        images = caps.maxImageCount;
    }
    VkSwapchainCreateInfoKHR si = { VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR };
    si.surface = w->surface;
    si.minImageCount = images;
    si.imageFormat = w->format;
    si.imageColorSpace = w->color_space;
    si.imageExtent = w->extent;
    si.imageArrayLayers = 1;
    si.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    si.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    si.preTransform = caps.currentTransform;
    si.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    si.presentMode = VK_PRESENT_MODE_FIFO_KHR;
    si.clipped = VK_TRUE;
    if (vkCreateSwapchainKHR(c->device, &si, NULL, &w->swapchain) != VK_SUCCESS) {
        w->swapchain = VK_NULL_HANDLE;
        return false;
    }
    vkGetSwapchainImagesKHR(c->device, w->swapchain, &w->image_count, NULL);
    w->images = (VkImage*)calloc(w->image_count, sizeof(VkImage));
    w->views = (VkImageView*)calloc(w->image_count, sizeof(VkImageView));
    w->framebuffers = (VkFramebuffer*)calloc(w->image_count, sizeof(VkFramebuffer));
    w->render_finished = (VkSemaphore*)calloc(w->image_count, sizeof(VkSemaphore));
    if (!w->images || !w->views || !w->framebuffers || !w->render_finished) {
        return false;
    }
    vkGetSwapchainImagesKHR(c->device, w->swapchain, &w->image_count, w->images);
    return true;   /* swapchain_finish makes the views, framebuffers and semaphores once the render pass exists */
}

/* the views, framebuffers and semaphores of the swapchain images (after the render pass exists) */
static bool swapchain_finish(Context* c) {
    Window* w = &c->window;
    VkSemaphoreCreateInfo semi = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    for (uint32_t i = 0; i < w->image_count; i++) {
        VkImageViewCreateInfo vi = { VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        vi.image = w->images[i];
        vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
        vi.format = w->format;
        vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        vi.subresourceRange.levelCount = 1;
        vi.subresourceRange.layerCount = 1;
        if (vkCreateImageView(c->device, &vi, NULL, &w->views[i]) != VK_SUCCESS) {
            return false;
        }
        VkFramebufferCreateInfo fi = { VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO };
        fi.renderPass = w->render_pass;
        fi.attachmentCount = 1;
        fi.pAttachments = &w->views[i];
        fi.width = w->extent.width;
        fi.height = w->extent.height;
        fi.layers = 1;
        if (vkCreateFramebuffer(c->device, &fi, NULL, &w->framebuffers[i]) != VK_SUCCESS
            || vkCreateSemaphore(c->device, &semi, NULL, &w->render_finished[i]) != VK_SUCCESS) {
            return false;
        }
    }
    return true;
}

static bool render_pass_create(Context* c) {
    Window* w = &c->window;
    VkAttachmentDescription color = { 0 };
    color.format = w->format;
    color.samples = VK_SAMPLE_COUNT_1_BIT;
    color.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    color.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    color.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    color.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    color.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    color.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    VkAttachmentReference ref = { 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
    VkSubpassDescription sub = { 0 };
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1;
    sub.pColorAttachments = &ref;
    VkSubpassDependency dep = { 0 };
    dep.srcSubpass = VK_SUBPASS_EXTERNAL;
    dep.srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep.dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    VkRenderPassCreateInfo ri = { VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO };
    ri.attachmentCount = 1;
    ri.pAttachments = &color;
    ri.subpassCount = 1;
    ri.pSubpasses = &sub;
    ri.dependencyCount = 1;
    ri.pDependencies = &dep;
    return vkCreateRenderPass(c->device, &ri, NULL, &w->render_pass) == VK_SUCCESS;
}

static VkShaderModule shader_module(Context* c, const uint32_t* code, size_t size) {
    VkShaderModuleCreateInfo ci = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
    ci.codeSize = size;
    ci.pCode = code;
    VkShaderModule module = VK_NULL_HANDLE;
    vkCreateShaderModule(c->device, &ci, NULL, &module);
    return module;
}

static bool pipeline_create(Context* c) {
    Window* w = &c->window;
    VkShaderModule vert = shader_module(c, gfx_vert_spv, sizeof gfx_vert_spv);
    VkShaderModule frag = shader_module(c, gfx_frag_spv, sizeof gfx_frag_spv);
    if (!vert || !frag) {
        if (vert) vkDestroyShaderModule(c->device, vert, NULL);
        if (frag) vkDestroyShaderModule(c->device, frag, NULL);
        return false;
    }
    VkPipelineShaderStageCreateInfo stages[2] = { { VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO },
                                                  { VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO } };
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
    stages[0].module = vert;
    stages[0].pName = "main";
    stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
    stages[1].module = frag;
    stages[1].pName = "main";

    VkVertexInputBindingDescription binding = { 0, sizeof(Vertex), VK_VERTEX_INPUT_RATE_VERTEX };
    VkVertexInputAttributeDescription attrs[3] = {
        { 0, 0, VK_FORMAT_R32G32_SFLOAT, offsetof(Vertex, x) },
        { 1, 0, VK_FORMAT_R32G32_SFLOAT, offsetof(Vertex, u) },
        { 2, 0, VK_FORMAT_R32G32B32A32_SFLOAT, offsetof(Vertex, r) }
    };
    VkPipelineVertexInputStateCreateInfo vi = { VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO };
    vi.vertexBindingDescriptionCount = 1;
    vi.pVertexBindingDescriptions = &binding;
    vi.vertexAttributeDescriptionCount = 3;
    vi.pVertexAttributeDescriptions = attrs;
    VkPipelineInputAssemblyStateCreateInfo ia = { VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO };
    ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkPipelineViewportStateCreateInfo vp = { VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO };
    vp.viewportCount = 1;
    vp.scissorCount = 1;
    VkPipelineRasterizationStateCreateInfo rs = { VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO };
    rs.polygonMode = VK_POLYGON_MODE_FILL;
    rs.cullMode = VK_CULL_MODE_NONE;   /* a mirrored or rotated quad keeps showing */
    rs.frontFace = VK_FRONT_FACE_CLOCKWISE;
    rs.lineWidth = 1.0f;
    VkPipelineMultisampleStateCreateInfo ms = { VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO };
    ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    VkPipelineColorBlendAttachmentState blend = { 0 };
    blend.blendEnable = VK_TRUE;
    blend.srcColorBlendFactor = VK_BLEND_FACTOR_SRC_ALPHA;
    blend.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    blend.colorBlendOp = VK_BLEND_OP_ADD;
    blend.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    blend.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    blend.alphaBlendOp = VK_BLEND_OP_ADD;
    blend.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    VkPipelineColorBlendStateCreateInfo cb = { VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO };
    cb.attachmentCount = 1;
    cb.pAttachments = &blend;
    VkDynamicState dynamic[] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
    VkPipelineDynamicStateCreateInfo dyn = { VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO };
    dyn.dynamicStateCount = 2;
    dyn.pDynamicStates = dynamic;
    VkGraphicsPipelineCreateInfo pi = { VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO };
    pi.stageCount = 2;
    pi.pStages = stages;
    pi.pVertexInputState = &vi;
    pi.pInputAssemblyState = &ia;
    pi.pViewportState = &vp;
    pi.pRasterizationState = &rs;
    pi.pMultisampleState = &ms;
    pi.pColorBlendState = &cb;
    pi.pDynamicState = &dyn;
    pi.layout = c->pipeline_layout;
    pi.renderPass = w->render_pass;
    VkResult result = vkCreateGraphicsPipelines(c->device, VK_NULL_HANDLE, 1, &pi, NULL, &w->pipeline);
    vkDestroyShaderModule(c->device, vert, NULL);
    vkDestroyShaderModule(c->device, frag, NULL);
    return result == VK_SUCCESS;
}

/* the vertex buffers (one per frame) and the shared index buffer, for at least quads quads */
static bool quad_buffers_create(Context* c, uint32_t quads) {
    Window* w = &c->window;
    for (int i = 0; i < GFX_FRAMES; i++) {
        if (!buffer_create(c, (VkDeviceSize)quads * 4 * sizeof(Vertex), VK_BUFFER_USAGE_VERTEX_BUFFER_BIT, &w->vertices[i])) {
            return false;
        }
    }
    if (!buffer_create(c, (VkDeviceSize)quads * 6 * sizeof(uint32_t), VK_BUFFER_USAGE_INDEX_BUFFER_BIT, &w->indices)) {
        return false;
    }
    uint32_t* idx = (uint32_t*)w->indices.mapped;
    for (uint32_t q = 0; q < quads; q++) {
        uint32_t base = q * 4;
        idx[q * 6 + 0] = base;
        idx[q * 6 + 1] = base + 1;
        idx[q * 6 + 2] = base + 2;
        idx[q * 6 + 3] = base;
        idx[q * 6 + 4] = base + 2;
        idx[q * 6 + 5] = base + 3;
    }
    w->quad_capacity = quads;
    return true;
}

static void quad_buffers_destroy(Context* c) {
    for (int i = 0; i < GFX_FRAMES; i++) {
        buffer_destroy(c, &c->window.vertices[i]);
    }
    buffer_destroy(c, &c->window.indices);
    c->window.quad_capacity = 0;
}

static void window_destroy(Context* c) {
    Window* w = &c->window;
    if (!w->exists) {
        return;
    }
    swapchain_destroy(c);
    quad_buffers_destroy(c);
    for (int i = 0; i < GFX_FRAMES; i++) {
        if (w->image_available[i]) vkDestroySemaphore(c->device, w->image_available[i], NULL);
        if (w->in_flight[i]) vkDestroyFence(c->device, w->in_flight[i], NULL);
    }
    if (w->cmd[0]) vkFreeCommandBuffers(c->device, c->command_pool, GFX_FRAMES, w->cmd);
    if (w->pipeline) vkDestroyPipeline(c->device, w->pipeline, NULL);
    if (w->render_pass) vkDestroyRenderPass(c->device, w->render_pass, NULL);
    if (w->surface) vkDestroySurfaceKHR(c->instance, w->surface, NULL);
    if (w->glfw) glfwDestroyWindow(w->glfw);
    uint32_t generation = w->generation == UINT32_MAX ? 1 : w->generation + 1;   /* the old handle becomes stale */
    memset(w, 0, sizeof *w);
    w->generation = generation;
}

uint64_t gfx_window_create(void* ctx, int32_t width, int32_t height) {
    Context* c = (Context*)ctx;
    if (!c || c->window.exists || width <= 0 || height <= 0) {
        return 0;
    }
    Window* w = &c->window;
    w->exists = true;
    w->world_width = width;
    w->world_height = height;
    w->resize_mode = GFX_RESIZE_KEEP_PIXEL_SIZE;
    glfwWindowHint(GLFW_CLIENT_API, GLFW_NO_API);
    glfwWindowHint(GLFW_RESIZABLE, GLFW_TRUE);
    w->glfw = glfwCreateWindow(width, height, "Dflat", NULL, NULL);
    if (!w->glfw) {
        window_destroy(c);
        return 0;
    }
    glfwSetWindowUserPointer(w->glfw, c);
    glfwSetFramebufferSizeCallback(w->glfw, on_framebuffer_resize);
    VkBool32 supported = VK_FALSE;
    VkSemaphoreCreateInfo semi = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    VkFenceCreateInfo fi = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
    fi.flags = VK_FENCE_CREATE_SIGNALED_BIT;
    VkCommandBufferAllocateInfo ai = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    ai.commandPool = c->command_pool;
    ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = GFX_FRAMES;
    bool ok = glfwCreateWindowSurface(c->instance, w->glfw, NULL, &w->surface) == VK_SUCCESS
        && vkGetPhysicalDeviceSurfaceSupportKHR(c->physical, c->family, w->surface, &supported) == VK_SUCCESS && supported
        && swapchain_create(c, false) && render_pass_create(c) && swapchain_finish(c) && pipeline_create(c)
        && vkAllocateCommandBuffers(c->device, &ai, w->cmd) == VK_SUCCESS
        && quad_buffers_create(c, GFX_FIRST_QUADS);
    for (int i = 0; ok && i < GFX_FRAMES; i++) {
        ok = vkCreateSemaphore(c->device, &semi, NULL, &w->image_available[i]) == VK_SUCCESS
            && vkCreateFence(c->device, &fi, NULL, &w->in_flight[i]) == VK_SUCCESS;
    }
    if (!ok) {
        fprintf(stderr, "[gfx] the window could not be created\n");
        window_destroy(c);
        return 0;
    }
    return make_handle(w->generation, KIND_WINDOW, 0);
}

static bool swapchain_recreate(Context* c) {
    vkDeviceWaitIdle(c->device);
    swapchain_destroy(c);
    return swapchain_create(c, true) && swapchain_finish(c);
}

int32_t gfx_window_width(void* ctx, uint64_t window) {
    Window* w = lookup_window((Context*)ctx, window);
    int fw = 0, fh = 0;
    if (w) glfwGetFramebufferSize(w->glfw, &fw, &fh);
    return fw;
}

int32_t gfx_window_height(void* ctx, uint64_t window) {
    Window* w = lookup_window((Context*)ctx, window);
    int fw = 0, fh = 0;
    if (w) glfwGetFramebufferSize(w->glfw, &fw, &fh);
    return fh;
}

void gfx_window_set_resize_mode(void* ctx, uint64_t window, int32_t mode) {
    Window* w = lookup_window((Context*)ctx, window);
    if (w && mode >= GFX_RESIZE_NONE && mode <= GFX_RESIZE_FIT_HEIGHT) {
        w->resize_mode = mode;
        glfwSetWindowAttrib(w->glfw, GLFW_RESIZABLE, mode == GFX_RESIZE_NONE ? GLFW_FALSE : GLFW_TRUE);
    }
}

void gfx_window_set_integer_scale(void* ctx, uint64_t window, bool integer_scale) {
    Window* w = lookup_window((Context*)ctx, window);
    if (w) {
        w->integer_scale = integer_scale;
    }
}

/* ------------------------------------------------------------------ */
/*  Textures, fonts                                                   */
/* ------------------------------------------------------------------ */

uint64_t gfx_texture_create(void* ctx, uint8_t* data, size_t length, int32_t width, int32_t height, int32_t format, int32_t filter) {
    Context* c = (Context*)ctx;
    if (!c || !data || width <= 0 || height <= 0 || format != GFX_FORMAT_RGBA8 || (uint64_t)length < (uint64_t)width * (uint64_t)height * 4) {
        return 0;
    }
    uint32_t index = pool_alloc(&c->textures);
    if (index == UINT32_MAX) {
        return 0;
    }
    Texture* t = (Texture*)pool_at(&c->textures, index);
    t->width = width;
    t->height = height;
    t->filter = filter;

    VkImageCreateInfo ii = { VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO };
    ii.imageType = VK_IMAGE_TYPE_2D;
    ii.format = VK_FORMAT_R8G8B8A8_SRGB;
    ii.extent.width = (uint32_t)width;
    ii.extent.height = (uint32_t)height;
    ii.extent.depth = 1;
    ii.mipLevels = 1;
    ii.arrayLayers = 1;
    ii.samples = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling = VK_IMAGE_TILING_OPTIMAL;
    ii.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
    ii.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    bool ok = vkCreateImage(c->device, &ii, NULL, &t->image) == VK_SUCCESS;
    if (ok) {
        VkMemoryRequirements req;
        vkGetImageMemoryRequirements(c->device, t->image, &req);
        VkMemoryAllocateInfo ai = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
        ai.allocationSize = req.size;
        ai.memoryTypeIndex = find_memory_type(c, req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        ok = ai.memoryTypeIndex != UINT32_MAX && vkAllocateMemory(c->device, &ai, NULL, &t->memory) == VK_SUCCESS
            && vkBindImageMemory(c->device, t->image, t->memory, 0) == VK_SUCCESS;
    }
    ok = ok && upload_pixels(c, t->image, width, height, data, true);
    if (ok) {
        VkImageViewCreateInfo vi = { VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        vi.image = t->image;
        vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
        vi.format = VK_FORMAT_R8G8B8A8_SRGB;
        vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        vi.subresourceRange.levelCount = 1;
        vi.subresourceRange.layerCount = 1;
        ok = vkCreateImageView(c->device, &vi, NULL, &t->view) == VK_SUCCESS && (t->sampler = sampler_create(c, filter)) != VK_NULL_HANDLE;
    }
    if (ok) {
        VkDescriptorSetAllocateInfo ai = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO };
        ai.descriptorPool = c->descriptor_pool;
        ai.descriptorSetCount = 1;
        ai.pSetLayouts = &c->set_layout;
        ok = vkAllocateDescriptorSets(c->device, &ai, &t->set) == VK_SUCCESS;
        if (!ok) {
            t->set = VK_NULL_HANDLE;
        }
    }
    if (!ok) {
        fprintf(stderr, "[gfx] the texture could not be created\n");
        texture_destroy(c, t);
        pool_free_slot(&t->slot);
        return 0;
    }
    descriptor_write(c, t);
    return make_handle(t->slot.generation, KIND_TEXTURE, index);
}

bool gfx_texture_set_data(void* ctx, uint64_t texture, uint8_t* data, size_t length) {
    Context* c = (Context*)ctx;
    Texture* t = (Texture*)lookup(c, texture, KIND_TEXTURE);
    if (!t || !data || (uint64_t)length < (uint64_t)t->width * (uint64_t)t->height * 4) {
        return false;
    }
    vkDeviceWaitIdle(c->device);   /* a frame in flight may read the image */
    return upload_pixels(c, t->image, t->width, t->height, data, false);
}

void gfx_texture_set_filter(void* ctx, uint64_t texture, int32_t filter) {
    Context* c = (Context*)ctx;
    Texture* t = (Texture*)lookup(c, texture, KIND_TEXTURE);
    if (!t || t->filter == filter) {
        return;
    }
    VkSampler sampler = sampler_create(c, filter);
    if (!sampler) {
        return;
    }
    vkDeviceWaitIdle(c->device);   /* the descriptor set cannot change while a frame uses it */
    vkDestroySampler(c->device, t->sampler, NULL);
    t->sampler = sampler;
    t->filter = filter;
    descriptor_write(c, t);
}

int32_t gfx_texture_width(void* ctx, uint64_t texture) {
    Texture* t = (Texture*)lookup((Context*)ctx, texture, KIND_TEXTURE);
    return t ? t->width : 0;
}

int32_t gfx_texture_height(void* ctx, uint64_t texture) {
    Texture* t = (Texture*)lookup((Context*)ctx, texture, KIND_TEXTURE);
    return t ? t->height : 0;
}

uint64_t gfx_font_create(void* ctx, uint64_t texture, int32_t glyph_width, int32_t glyph_height) {
    Context* c = (Context*)ctx;
    if (!lookup(c, texture, KIND_TEXTURE) || glyph_width <= 0 || glyph_height <= 0) {
        return 0;
    }
    uint32_t index = pool_alloc(&c->fonts);
    if (index == UINT32_MAX) {
        return 0;
    }
    Font* f = (Font*)pool_at(&c->fonts, index);
    f->texture = texture;
    f->glyph_width = glyph_width;
    f->glyph_height = glyph_height;
    return make_handle(f->slot.generation, KIND_FONT, index);
}

/* ------------------------------------------------------------------ */
/*  Sprites, labels                                                   */
/* ------------------------------------------------------------------ */

uint64_t gfx_sprite_create(void* ctx, uint64_t texture) {
    Context* c = (Context*)ctx;
    if (!lookup(c, texture, KIND_TEXTURE)) {
        return 0;
    }
    uint32_t index = pool_alloc(&c->sprites);
    if (index == UINT32_MAX) {
        return 0;
    }
    Sprite* s = (Sprite*)pool_at(&c->sprites, index);
    s->texture = texture;
    s->scale_x = s->scale_y = 1.0f;
    s->br_x = s->br_y = 1.0f;
    s->visible = true;
    s->serial = ++c->serial;
    return make_handle(s->slot.generation, KIND_SPRITE, index);
}

void gfx_sprite_set_texture(void* ctx, uint64_t sprite, uint64_t texture) {
    Context* c = (Context*)ctx;
    Sprite* s = (Sprite*)lookup(c, sprite, KIND_SPRITE);
    if (s && lookup(c, texture, KIND_TEXTURE)) {
        s->texture = texture;
    }
}

void gfx_sprite_set_state(void* ctx, uint64_t sprite, float pos_x, float pos_y, float offset_x, float offset_y,
                          float scale_x, float scale_y, float rotation, int32_t order,
                          float tl_x, float tl_y, float br_x, float br_y, bool visible) {
    Sprite* s = (Sprite*)lookup((Context*)ctx, sprite, KIND_SPRITE);
    if (!s) {
        return;
    }
    s->pos_x = pos_x;
    s->pos_y = pos_y;
    s->offset_x = offset_x;
    s->offset_y = offset_y;
    s->scale_x = scale_x;
    s->scale_y = scale_y;
    s->rotation = rotation;
    s->order = order;
    s->tl_x = tl_x;
    s->tl_y = tl_y;
    s->br_x = br_x;
    s->br_y = br_y;
    s->visible = visible;
}

static char* copy_text(const char* text) {
    size_t length = text ? strlen(text) : 0;
    char* copy = (char*)malloc(length + 1);
    if (copy) {
        if (length > 0) memcpy(copy, text, length);
        copy[length] = 0;
    }
    return copy;
}

uint64_t gfx_label_create(void* ctx, uint64_t font, const char* text) {
    Context* c = (Context*)ctx;
    if (!lookup(c, font, KIND_FONT)) {
        return 0;
    }
    char* copy = copy_text(text);
    uint32_t index = copy ? pool_alloc(&c->labels) : UINT32_MAX;
    if (index == UINT32_MAX) {
        free(copy);
        return 0;
    }
    Label* l = (Label*)pool_at(&c->labels, index);
    l->font = font;
    l->text = copy;
    l->scale_x = l->scale_y = 1.0f;
    l->visible = true;
    l->color[0] = l->color[1] = l->color[2] = l->color[3] = 1.0f;
    l->serial = ++c->serial;
    return make_handle(l->slot.generation, KIND_LABEL, index);
}

void gfx_label_set_text(void* ctx, uint64_t label, const char* text) {
    Label* l = (Label*)lookup((Context*)ctx, label, KIND_LABEL);
    char* copy = l ? copy_text(text) : NULL;
    if (copy) {
        free(l->text);
        l->text = copy;
    }
}

void gfx_label_set_font(void* ctx, uint64_t label, uint64_t font) {
    Context* c = (Context*)ctx;
    Label* l = (Label*)lookup(c, label, KIND_LABEL);
    if (l && lookup(c, font, KIND_FONT)) {
        l->font = font;
    }
}

void gfx_label_set_state(void* ctx, uint64_t label, float pos_x, float pos_y, float offset_x, float offset_y,
                         float scale_x, float scale_y, float rotation, int32_t order, bool visible,
                         float r, float g, float b, float a) {
    Label* l = (Label*)lookup((Context*)ctx, label, KIND_LABEL);
    if (!l) {
        return;
    }
    l->pos_x = pos_x;
    l->pos_y = pos_y;
    l->offset_x = offset_x;
    l->offset_y = offset_y;
    l->scale_x = scale_x;
    l->scale_y = scale_y;
    l->rotation = rotation;
    l->order = order;
    l->visible = visible;
    l->color[0] = r;
    l->color[1] = g;
    l->color[2] = b;
    l->color[3] = a;
}

/* ------------------------------------------------------------------ */
/*  The frame                                                         */
/* ------------------------------------------------------------------ */

typedef struct {
    int32_t  order;
    uint64_t serial;
    bool     is_label;
    uint32_t index;
} DrawItem;

static int draw_item_compare(const void* a, const void* b) {
    const DrawItem* x = (const DrawItem*)a;
    const DrawItem* y = (const DrawItem*)b;
    if (x->order != y->order) return x->order < y->order ? -1 : 1;
    if (x->serial != y->serial) return x->serial < y->serial ? -1 : 1;
    return 0;
}

/* the part of the framebuffer the world is drawn in (vx, vy, vw, vh) and the size of the world shown there (ww, wh) */
static void compute_view(const Window* w, float fbw, float fbh, float* vx, float* vy, float* vw, float* vh, float* ww, float* wh) {
    float W = (float)w->world_width, H = (float)w->world_height;
    *vx = 0; *vy = 0; *vw = fbw; *vh = fbh; *ww = fbw; *wh = fbh;
    switch (w->resize_mode) {
        case GFX_RESIZE_STRETCH: {
            float sx = fbw / W, sy = fbh / H;
            if (w->integer_scale) {
                sx = fmaxf(1.0f, floorf(sx));
                sy = fmaxf(1.0f, floorf(sy));
            }
            *vw = W * sx; *vh = H * sy; *ww = W; *wh = H;
            break;
        }
        case GFX_RESIZE_FIT: {
            float s = fminf(fbw / W, fbh / H);
            if (w->integer_scale) s = fmaxf(1.0f, floorf(s));
            *vw = W * s; *vh = H * s; *ww = W; *wh = H;
            break;
        }
        case GFX_RESIZE_FIT_WIDTH: {
            float s = fbw / W;
            if (w->integer_scale) s = fmaxf(1.0f, floorf(s));
            *vw = W * s; *ww = W; *wh = fbh / s;
            break;
        }
        case GFX_RESIZE_FIT_HEIGHT: {
            float s = fbh / H;
            if (w->integer_scale) s = fmaxf(1.0f, floorf(s));
            *vh = H * s; *wh = H; *ww = fbw / s;
            break;
        }
        default:   /* None, KeepPixelSize: one world unit is one pixel */
            break;
    }
    *vx = floorf((fbw - *vw) / 2);
    *vy = floorf((fbh - *vh) / 2);
}

/* column-major orthographic projection: x 0..ww to -1..1, y 0..wh to -1..1 (Vulkan: -1 is the top) */
static void ortho(float* m, float ww, float wh) {
    memset(m, 0, 16 * sizeof(float));
    m[0] = 2.0f / ww;
    m[5] = 2.0f / wh;
    m[10] = 1.0f;
    m[12] = -1.0f;
    m[13] = -1.0f;
    m[15] = 1.0f;
}

/* one quad: the corner (lx, ly) of the unscaled quad, relative to its top-left, goes to the world */
typedef struct {
    float pos_x, pos_y, offset_x, offset_y, scale_x, scale_y, cos_r, sin_r;
} Transform;

static void corner(const Transform* t, float lx, float ly, float* out_x, float* out_y) {
    float x = (lx - t->offset_x) * t->scale_x;
    float y = (ly - t->offset_y) * t->scale_y;
    *out_x = t->pos_x + x * t->cos_r - y * t->sin_r;
    *out_y = t->pos_y + x * t->sin_r + y * t->cos_r;
}

static void emit_quad(Vertex* v, const Transform* t, float x0, float y0, float x1, float y1,
                      float u0, float v0, float u1, float v1, const float* color) {
    float px[4], py[4];
    corner(t, x0, y0, &px[0], &py[0]);
    corner(t, x1, y0, &px[1], &py[1]);
    corner(t, x1, y1, &px[2], &py[2]);
    corner(t, x0, y1, &px[3], &py[3]);
    float us[4] = { u0, u1, u1, u0 };
    float vs[4] = { v0, v0, v1, v1 };
    for (int i = 0; i < 4; i++) {
        v[i].x = px[i];
        v[i].y = py[i];
        v[i].u = us[i];
        v[i].v = vs[i];
        v[i].r = color[0];
        v[i].g = color[1];
        v[i].b = color[2];
        v[i].a = color[3];
    }
}

static uint32_t label_quads(const Label* l) {
    uint32_t n = 0;
    for (const unsigned char* p = (const unsigned char*)l->text; p && *p; p++) {
        if (*p >= 32 && *p <= 126) n++;
    }
    return n;
}

bool gfx_update(void* ctx) {
    Context* c = (Context*)ctx;
    if (!c || !c->window.exists) {
        return false;
    }
    Window* w = &c->window;
    glfwPollEvents();
    if (glfwWindowShouldClose(w->glfw)) {
        return false;
    }
    int fbw = 0, fbh = 0;
    glfwGetFramebufferSize(w->glfw, &fbw, &fbh);
    if (fbw == 0 || fbh == 0) {
        return true;   /* minimized: nothing to draw, the program goes on */
    }
    if (w->resized) {
        w->resized = false;
        if (!swapchain_recreate(c)) {
            return false;
        }
    }

    uint32_t frame = w->frame;
    vkWaitForFences(c->device, 1, &w->in_flight[frame], VK_TRUE, UINT64_MAX);
    uint32_t image = 0;
    VkResult result = vkAcquireNextImageKHR(c->device, w->swapchain, UINT64_MAX, w->image_available[frame], VK_NULL_HANDLE, &image);
    if (result == VK_ERROR_OUT_OF_DATE_KHR) {
        return swapchain_recreate(c);
    }
    if (result != VK_SUCCESS && result != VK_SUBOPTIMAL_KHR) {
        fprintf(stderr, "[gfx] the swapchain image could not be acquired\n");
        return false;
    }

    /* what is drawn: visible sprites with a live texture, visible labels with a live font and texture */
    uint32_t item_count = 0, quad_count = 0;
    DrawItem* items = (DrawItem*)malloc(((size_t)c->sprites.count + c->labels.count + 1) * sizeof(DrawItem));
    if (!items) {
        return false;
    }
    for (uint32_t i = 0; i < c->sprites.count; i++) {
        Sprite* s = (Sprite*)pool_at(&c->sprites, i);
        if (s->slot.used && s->visible && lookup(c, s->texture, KIND_TEXTURE)) {
            items[item_count++] = (DrawItem){ s->order, s->serial, false, i };
            quad_count++;
        }
    }
    for (uint32_t i = 0; i < c->labels.count; i++) {
        Label* l = (Label*)pool_at(&c->labels, i);
        Font* f = l->slot.used && l->visible ? (Font*)lookup(c, l->font, KIND_FONT) : NULL;
        uint32_t n = f && lookup(c, f->texture, KIND_TEXTURE) ? label_quads(l) : 0;
        if (n > 0) {
            items[item_count++] = (DrawItem){ l->order, l->serial, true, i };
            quad_count += n;
        }
    }
    qsort(items, item_count, sizeof(DrawItem), draw_item_compare);

    if (quad_count > w->quad_capacity) {
        uint32_t capacity = w->quad_capacity;
        while (capacity < quad_count) capacity *= 2;
        vkDeviceWaitIdle(c->device);
        quad_buffers_destroy(c);
        if (!quad_buffers_create(c, capacity)) {
            free(items);
            return false;
        }
    }

    /* the vertices, and the runs of quads with the same texture (one draw each) */
    Vertex* v = (Vertex*)w->vertices[frame].mapped;
    VkDescriptorSet* run_sets = (VkDescriptorSet*)malloc(((size_t)item_count + 1) * sizeof(VkDescriptorSet));
    uint32_t* run_quads = (uint32_t*)malloc(((size_t)item_count + 1) * sizeof(uint32_t));
    if (!run_sets || !run_quads) {
        free(items);
        free(run_sets);
        free(run_quads);
        return false;
    }
    uint32_t runs = 0, q = 0;
    static const float white[4] = { 1.0f, 1.0f, 1.0f, 1.0f };
    for (uint32_t i = 0; i < item_count; i++) {
        Texture* t;
        uint32_t added = 0;
        if (!items[i].is_label) {
            Sprite* s = (Sprite*)pool_at(&c->sprites, items[i].index);
            t = (Texture*)lookup(c, s->texture, KIND_TEXTURE);
            Transform tr = { s->pos_x, s->pos_y, s->offset_x, s->offset_y, s->scale_x, s->scale_y, cosf(s->rotation), sinf(s->rotation) };
            float qw = (s->br_x - s->tl_x) * (float)t->width;
            float qh = (s->br_y - s->tl_y) * (float)t->height;
            emit_quad(&v[q * 4], &tr, 0, 0, qw, qh, s->tl_x, s->tl_y, s->br_x, s->br_y, white);
            added = 1;
        } else {
            Label* l = (Label*)pool_at(&c->labels, items[i].index);
            Font* f = (Font*)lookup(c, l->font, KIND_FONT);
            t = (Texture*)lookup(c, f->texture, KIND_TEXTURE);
            Transform tr = { l->pos_x, l->pos_y, l->offset_x, l->offset_y, l->scale_x, l->scale_y, cosf(l->rotation), sinf(l->rotation) };
            int32_t columns = t->width / f->glyph_width;
            float gw = (float)f->glyph_width, gh = (float)f->glyph_height;
            float x = 0, y = 0;
            for (const unsigned char* p = (const unsigned char*)l->text; *p; p++) {
                if (*p == '\n') {
                    x = 0;
                    y += gh;
                    continue;
                }
                if (*p < 32 || *p > 126) {
                    continue;   /* skipped, as the spec says */
                }
                int32_t glyph = *p - 32;
                int32_t col = columns > 0 ? glyph % columns : 0;
                int32_t row = columns > 0 ? glyph / columns : 0;
                float u0 = col * gw / (float)t->width, v0 = row * gh / (float)t->height;
                float u1 = (col + 1) * gw / (float)t->width, v1 = (row + 1) * gh / (float)t->height;
                emit_quad(&v[(q + added) * 4], &tr, x, y, x + gw, y + gh, u0, v0, u1, v1, l->color);
                added++;
                x += gw;
            }
        }
        if (runs > 0 && run_sets[runs - 1] == t->set) {
            run_quads[runs - 1] += added;
        } else {
            run_sets[runs] = t->set;
            run_quads[runs] = added;
            runs++;
        }
        q += added;
    }
    free(items);

    /* the commands */
    VkCommandBuffer cmd = w->cmd[frame];
    vkResetFences(c->device, 1, &w->in_flight[frame]);
    vkResetCommandBuffer(cmd, 0);
    VkCommandBufferBeginInfo bi = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
    vkBeginCommandBuffer(cmd, &bi);
    VkClearValue bars = { { { 0.0f, 0.0f, 0.0f, 1.0f } } };   /* the bars of the scaling modes are black */
    VkRenderPassBeginInfo rp = { VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO };
    rp.renderPass = w->render_pass;
    rp.framebuffer = w->framebuffers[image];
    rp.renderArea.extent = w->extent;
    rp.clearValueCount = 1;
    rp.pClearValues = &bars;
    vkCmdBeginRenderPass(cmd, &rp, VK_SUBPASS_CONTENTS_INLINE);

    float vx, vy, vw, vh, ww, wh;
    compute_view(w, (float)w->extent.width, (float)w->extent.height, &vx, &vy, &vw, &vh, &ww, &wh);
    VkViewport viewport = { vx, vy, vw, vh, 0.0f, 1.0f };
    /* the scissor: the viewport inside the framebuffer */
    int32_t sx0 = (int32_t)fmaxf(vx, 0), sy0 = (int32_t)fmaxf(vy, 0);
    int32_t sx1 = (int32_t)fminf(vx + vw, (float)w->extent.width), sy1 = (int32_t)fminf(vy + vh, (float)w->extent.height);
    VkRect2D scissor = { { sx0, sy0 }, { (uint32_t)(sx1 > sx0 ? sx1 - sx0 : 0), (uint32_t)(sy1 > sy0 ? sy1 - sy0 : 0) } };
    if (scissor.extent.width > 0 && scissor.extent.height > 0) {
        VkClearAttachment clear = { VK_IMAGE_ASPECT_COLOR_BIT, 0, { { { c->clear[0], c->clear[1], c->clear[2], c->clear[3] } } } };
        VkClearRect rect = { scissor, 0, 1 };
        vkCmdClearAttachments(cmd, 1, &clear, 1, &rect);   /* the world starts from the clear color */
        vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, w->pipeline);
        vkCmdSetViewport(cmd, 0, 1, &viewport);
        vkCmdSetScissor(cmd, 0, 1, &scissor);
        float m[16];
        ortho(m, ww, wh);
        vkCmdPushConstants(cmd, c->pipeline_layout, VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof m, m);
        if (q > 0) {
            VkDeviceSize offset = 0;
            vkCmdBindVertexBuffers(cmd, 0, 1, &w->vertices[frame].buffer, &offset);
            vkCmdBindIndexBuffer(cmd, w->indices.buffer, 0, VK_INDEX_TYPE_UINT32);
            uint32_t first = 0;
            for (uint32_t r = 0; r < runs; r++) {
                vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, c->pipeline_layout, 0, 1, &run_sets[r], 0, NULL);
                vkCmdDrawIndexed(cmd, run_quads[r] * 6, 1, first * 6, 0, 0);
                first += run_quads[r];
            }
        }
    }
    free(run_sets);
    free(run_quads);
    vkCmdEndRenderPass(cmd);
    vkEndCommandBuffer(cmd);

    VkPipelineStageFlags wait_stage = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    VkSubmitInfo si = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
    si.waitSemaphoreCount = 1;
    si.pWaitSemaphores = &w->image_available[frame];
    si.pWaitDstStageMask = &wait_stage;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    si.signalSemaphoreCount = 1;
    si.pSignalSemaphores = &w->render_finished[image];
    if (vkQueueSubmit(c->queue, 1, &si, w->in_flight[frame]) != VK_SUCCESS) {
        fprintf(stderr, "[gfx] the frame could not be submitted\n");
        return false;
    }
    VkPresentInfoKHR pi = { VK_STRUCTURE_TYPE_PRESENT_INFO_KHR };
    pi.waitSemaphoreCount = 1;
    pi.pWaitSemaphores = &w->render_finished[image];
    pi.swapchainCount = 1;
    pi.pSwapchains = &w->swapchain;
    pi.pImageIndices = &image;
    result = vkQueuePresentKHR(c->queue, &pi);
    w->frame = (frame + 1) % GFX_FRAMES;
    if (result == VK_ERROR_OUT_OF_DATE_KHR || result == VK_SUBOPTIMAL_KHR) {
        return swapchain_recreate(c);
    }
    return result == VK_SUCCESS;
}
