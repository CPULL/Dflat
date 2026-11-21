namespace dfcompiler;

// C runtime of the tasks (spec 20.1, 20.2): added with the runtime chunk "tasks" (import Root.Tasks), which also defines DF_TASKS.
// A task owns the object of its class and runs its taskRun() on an OS thread of its own. The Dflat side is the class Task of
// TasksPrelude (CodeGen.Tasks.cs): each Task object holds one reference to the task; the running thread holds one more. The last
// reference that goes ends the object of the task and frees the task.
//
// The values a task hands out (return, return!, return?) go through one slot, under the lock of the task. The waiters take a ticket
// and are served in that order (spec 20.2: FIFO); a waiter that runs out of time gives its turn up.
public static class RuntimeTasks {
  public const string Source = """
/* ---- tasks (spec 20.1, 20.2) ---- */

/* RunStatus (spec 20.9), in the order of the enum */
#define DF_RUN_READY 0
#define DF_RUN_RUNNING 1
#define DF_RUN_COMPLETED 2
#define DF_RUN_FAILED 3
#define DF_RUN_KILLED 4

/* how a value is handed out (spec 20.1) */
#define DF_GIVE_RETURN 0   /* return val: wait until it is collected (or the timeout), then the task ends */
#define DF_GIVE_HOLD 1     /* return! val: wait until it is collected (or the timeout), then go on */
#define DF_GIVE_POST 2     /* return? val: to whoever waits now, else dropped; go on at once */

/* a call through the handle (spec 20.1): made by the caller, run by the task at a checkpoint of its taskRun(). The generated record
   starts with this head, then the arguments and the result */
typedef struct df_task_call {
  struct df_task_call* next;
  void (*invoke)(void* obj, struct df_task_call* call);   /* runs the method on the object; its exception goes to error */
  void (*end)(struct df_task_call* call);                  /* ends the arguments, and a result nobody took */
  int64_t refs;                                            /* the queue of the task, and the caller when it waits */
  bool waited;
  bool done;
  df_exception* error;
} df_task_call;

typedef struct df_task {
  int64_t refs;                       /* the Task objects that hold it, and its thread while it runs */
  int32_t status;
  bool linked;                        /* true: a child of the task that made it; false: a child of the main program */
  int64_t timeout;                    /* milliseconds; -1: not given yet (spec 20.1: constructor < start < wait) */
  const char* name;                   /* the class of the task */
  const char* result;                 /* what taskRun() gives, as written in Dflat (Task<*, i32> checks it, spec 20.9) */
  void* obj;                          /* the object of the task, owned by the task; for a thread, the record of its call */
  void (*endObj)(void* obj);          /* ends obj: df_drop for the object of a task, the end of the record for a thread */
  void* threadFn;                     /* a thread: the C function it runs (return! and return? check it); NULL for a task */
  df_exception* (*run)(void* obj);    /* calls taskRun() of the object; gives the exception that left it */
  df_exception* failure;              /* the exception that ended the task (Failed), until a waiter takes it */
  struct df_task* parent;             /* NULL: the main program */
  /* the values handed out */
  df_mutex lock;
  df_cond changed;
  size_t size;                        /* the size of a result of taskRun(); 0 for a void taskRun() */
  void (*drop)(void*);                /* ends a value nobody collected */
  void* slot;                         /* the value waiting to be collected */
  bool full;
  bool ended;                         /* taskRun() is over: no value will come */
  bool endTaken;                      /* a void task: a waiter saw its end (the next waits get TaskAlreadyTerminatedException) */
  uint64_t nextTicket;                /* the waiters, in order */
  uint64_t serving;
  int64_t waiting;
  uint64_t* given;                    /* the tickets of the waiters that ran out of time, not yet reached */
  size_t givenCount;
  size_t givenCap;
  /* the calls through the handle, in order */
  df_task_call* callsHead;
  df_task_call* callsTail;
  df_exception* postedError;          /* the exception of a posted call: raised at the next checkpoint (spec 20.1) */
} df_task;

/* the task this thread runs; NULL in the main program */
static DF_TLS df_task* df_task_current = NULL;

/* a lapse in milliseconds, -1 for none (a null lapse); a number is already a lapse in milliseconds (spec 3.4) */
static int64_t df_task_lapse_ms(int64_t l) {
  int64_t value = l >> 3;
  if (l == 0) {
    return -1;
  }
  switch (l & 7) {
    case 2: return value < 0 ? 0 : value;
    case 3: return value < 0 ? 0 : (value + 9999) / 10000;
    default: return -1;
  }
}

static uint64_t df_task_new(void* obj, df_exception* (*run)(void*), const char* name, const char* result, int64_t timeout, bool linked,
    size_t size, void (*drop)(void*)) {
  df_task* task = (df_task*)df_alloc(sizeof(df_task));
  memset(task, 0, sizeof(df_task));
  task->refs = 1;
  task->status = DF_RUN_READY;
  task->linked = linked;
  task->timeout = df_task_lapse_ms(timeout);
  task->name = name;
  task->result = result;
  task->obj = obj;
  task->endObj = df_drop;
  task->threadFn = NULL;
  task->run = run;
  task->parent = linked ? df_task_current : NULL;
  df_mutex_init(&task->lock);
  df_cond_init(&task->changed);
  task->size = size;
  task->drop = drop;
  task->slot = size ? df_alloc(size) : NULL;
  return (uint64_t)(uintptr_t)task;
}

/* a thread (spec 20.1): a task whose object is the record of the call of its function; it belongs to the task that makes it */
static uint64_t df_thread_new(void* record, void (*endRecord)(void*), df_exception* (*run)(void*), void* fn, const char* name, const char* result,
    int64_t timeout, size_t size, void (*drop)(void*)) {
  uint64_t handle = df_task_new(record, run, name, result, timeout, true, size, drop);
  df_task* task = (df_task*)(uintptr_t)handle;
  task->endObj = endRecord;
  task->threadFn = fn;
  return handle;
}

static uint64_t df_task_retain(uint64_t handle) {
  if (handle) {
    DF_ATOMIC_ADD(((df_task*)(uintptr_t)handle)->refs, 1);
  }
  return handle;
}

static df_task_call* df_task_call_new(size_t size, void (*invoke)(void*, df_task_call*), void (*end)(df_task_call*), bool waited) {
  df_task_call* call = (df_task_call*)df_alloc(size);
  memset(call, 0, size);
  call->invoke = invoke;
  call->end = end;
  call->refs = waited ? 2 : 1;
  call->waited = waited;
  return call;
}

static void df_task_call_release(df_task_call* call) {
  if (call != NULL && DF_ATOMIC_SUB(call->refs, 1) == 0) {
    call->end(call);
    if (call->error) {
      df_exc_release(call->error);
    }
    free(call);
  }
}

static void df_task_release(uint64_t handle) {
  df_task* task = (df_task*)(uintptr_t)handle;
  if (task == NULL || DF_ATOMIC_SUB(task->refs, 1) != 0) {
    return;
  }
  if (task->full && task->drop) {
    task->drop(task->slot);
  }
  if (task->obj) {
    task->endObj(task->obj);
  }
  if (task->failure) {
    df_exc_release(task->failure);
  }
  if (task->postedError) {
    df_exc_release(task->postedError);
  }
  while (task->callsHead) {
    df_task_call* call = task->callsHead;   /* calls that were never served */
    task->callsHead = call->next;
    df_task_call_release(call);
  }
  df_cond_destroy(&task->changed);
  df_mutex_destroy(&task->lock);
  free(task->slot);
  free(task->given);
  free(task);
}

/* the state of one execution that a thread leaves behind when it ends (DF_TLS in the runtime) */
static void df_thread_cleanup(void) {
  free(df_tmps);
  free(df_tmp_objs);
  free(df_tmp_fns);
  df_tmps = NULL;
  df_tmp_objs = NULL;
  df_tmp_fns = NULL;
  df_tmp_top = 0;
  df_tmp_cap = 0;
  for (unsigned i = 0; i < DF_FLAT_RING; i++) {
    free(df_flat_ring[i]);
    df_flat_ring[i] = NULL;
  }
}

/* the turns of the waiters that ran out of time are skipped (the lock is held) */
static void df_task_skip_given(df_task* task) {
  for (size_t i = 0; i < task->givenCount;) {
    if (task->given[i] == task->serving) {
      task->serving++;
      task->given[i] = task->given[--task->givenCount];
      i = 0;
    } else {
      i++;
    }
  }
}

/* the task runs the calls waiting in its queue (the lock is NOT held), each one alone: a call never runs inside another (spec 20.1).
   The exception of a posted call is kept for the checkpoint that raises it; the first one only */
static void df_task_serve_calls(df_task* task) {
  for (;;) {
    df_mutex_lock(&task->lock);
    df_task_call* call = task->callsHead;
    if (call) {
      task->callsHead = call->next;
      if (task->callsHead == NULL) {
        task->callsTail = NULL;
      }
    }
    df_mutex_unlock(&task->lock);
    if (call == NULL) {
      return;
    }
    call->invoke(task->obj, call);
    if (call->error && call->error->descriptor->silent) {
      df_exc_release(call->error);
      call->error = NULL;
    }
    df_mutex_lock(&task->lock);
    if (!call->waited && call->error) {
      if (task->postedError == NULL) {
        task->postedError = call->error;
      } else {
        df_exc_release(call->error);
      }
      call->error = NULL;
    }
    call->done = true;
    df_cond_broadcast(&task->changed);
    df_mutex_unlock(&task->lock);
    df_task_call_release(call);
  }
}

/* the exception of a posted call, raised at a checkpoint (df_pending) */
static void df_task_raise_posted(df_task* task) {
  df_mutex_lock(&task->lock);
  df_exception* posted = task->postedError;
  task->postedError = NULL;
  df_mutex_unlock(&task->lock);
  if (posted) {
    df_pending = posted;
  }
}

/* the task that runs the object self, when the code runs as that task; NULL otherwise (a direct call of taskRun) */
static df_task* df_task_of(void* self) {
  df_task* task = df_task_current;
  return task != NULL && task->obj == self ? task : NULL;
}

/* a checkpoint of taskRun() (spec 20.8): a wait, an enforce, a return of any kind: the calls through the handle are served here */
static void df_task_checkpoint(void* self) {
  df_task* task = df_task_of(self);
  if (task) {
    df_task_serve_calls(task);
    df_task_raise_posted(task);
  }
}

/* wait 250 in taskRun() (spec 20.8): the time passes, and the calls that come meanwhile are served; elsewhere a plain sleep */
static void df_task_pause(int64_t lapse, void* self) {
  df_task* task = self ? df_task_of(self) : NULL;
  if (task == NULL) {
    df_time_sleep(lapse);
    return;
  }
  int64_t ms = df_task_lapse_ms(lapse);
  int64_t until = df_mono_ms() + (ms < 0 ? 0 : ms);
  for (;;) {
    df_task_serve_calls(task);
    int64_t left = until - df_mono_ms();
    if (left <= 0) {
      break;
    }
    df_mutex_lock(&task->lock);
    if (task->callsHead == NULL) {
      df_cond_wait_ms(&task->changed, &task->lock, left);
    }
    df_mutex_unlock(&task->lock);
  }
  df_task_raise_posted(task);
}

/* a call through the handle: queued for the task; false when the task is not running (CalledANotRunningTaskException, spec 20.1) */
static bool df_task_post_call(uint64_t handle, df_task_call* call) {
  df_task* task = (df_task*)(uintptr_t)handle;
  df_mutex_lock(&task->lock);
  if (task->status != DF_RUN_RUNNING || task->ended) {
    df_mutex_unlock(&task->lock);
    return false;
  }
  if (task->callsTail) {
    task->callsTail->next = call;
  } else {
    task->callsHead = call;
  }
  task->callsTail = call;
  df_cond_broadcast(&task->changed);
  df_mutex_unlock(&task->lock);
  return true;
}

/* wait t.method() [timeout n]: the caller waits for the call to be served; *got says whether its result is there. The exception
   of the method is the caller's; a timeout or a task that ends first leave a silent note */
static df_exception* df_task_wait_call(uint64_t handle, df_task_call* call, int64_t timeout, bool* got) {
  df_task* task = (df_task*)(uintptr_t)handle;
  *got = false;
  int64_t ms = df_task_lapse_ms(timeout);
  df_mutex_lock(&task->lock);
  if (ms < 0) {
    ms = task->timeout;
  }
  if (ms < 0) {
    df_mutex_unlock(&task->lock);
    const char* text = "The wait has no timeout: give one to the wait, to start or to the constructor of the task";
    return df_new_exception(&df_desc_WaitingWithoutTimeoutException, text, strlen(text), "wait");
  }
  int64_t until = df_mono_ms() + ms;
  df_exception* error = NULL;
  for (;;) {
    if (call->done) {
      *got = true;
      error = call->error;
      call->error = NULL;
      break;
    }
    if (task->ended) {
      df_silent_desc = &df_desc_TaskAlreadyTerminatedException;   /* the task ended before it served the call */
      break;
    }
    int64_t left = until - df_mono_ms();
    if (left <= 0) {
      df_silent_desc = &df_desc_WaitTimeoutException;
      break;
    }
    df_cond_wait_ms(&task->changed, &task->lock, left);
  }
  df_mutex_unlock(&task->lock);
  return error;
}
/* hands out a value of taskRun() (spec 20.1): the value moves into the task, or is ended when nobody collects it */
static void df_task_give(df_task* task, void* value, int mode) {
  if (task->size == 0) {
    return;
  }
  df_mutex_lock(&task->lock);
  if (mode == DF_GIVE_POST && task->waiting == 0) {
    df_mutex_unlock(&task->lock);
    if (task->drop) {
      task->drop(value);   /* return?: nobody waits now */
    }
    return;
  }
  if (task->full && task->drop) {
    task->drop(task->slot);   /* a posted value that its waiter did not take in time */
  }
  memcpy(task->slot, value, task->size);
  task->full = true;
  df_cond_broadcast(&task->changed);
  if (mode != DF_GIVE_POST) {
    int64_t ms = task->timeout;
    int64_t until = ms < 0 ? 0 : df_mono_ms() + ms;
    while (task->full) {
      if (task->callsHead) {
        df_mutex_unlock(&task->lock);   /* a return of any kind is a checkpoint: the calls are served while it waits (spec 20.8) */
        df_task_serve_calls(task);
        df_mutex_lock(&task->lock);
        continue;
      }
      int64_t left = ms < 0 ? -1 : until - df_mono_ms();
      if (ms >= 0 && left <= 0) {
        task->full = false;   /* nobody collected it in time: dropped (spec 20.1) */
        if (task->drop) {
          task->drop(task->slot);
        }
        break;
      }
      df_cond_wait_ms(&task->changed, &task->lock, left);
    }
  }
  df_mutex_unlock(&task->lock);
}

/* return! and return? in taskRun() (spec 20.1): only while the object runs as a task; false when it does not (a direct call) */
static bool df_task_give_from(void* self, void* value, int mode) {
  df_task* task = df_task_current;
  if (task == NULL || task->obj != self) {
    return false;
  }
  df_task_serve_calls(task);
  df_task_give(task, value, mode);
  df_task_raise_posted(task);
  return true;
}

/* return! and return? in the function of a thread: only while it runs as that thread; false otherwise (a direct call) */
static bool df_thread_give_from(void* fn, void* value, int mode) {
  df_task* task = df_task_current;
  if (task == NULL || task->threadFn != fn) {
    return false;
  }
  df_task_give(task, value, mode);
  return true;
}

static void df_task_main(void* p) {
  df_task* task = (df_task*)p;
  df_task_current = task;
  df_exception* failure = task->run(task->obj);   /* the last value (return val) is handed out inside run */
  df_mutex_lock(&task->lock);
  if (failure == NULL && task->postedError) {
    failure = task->postedError;   /* a posted call failed while the last value waited (spec 20.1) */
    task->postedError = NULL;
  }
  if (failure) {
    task->failure = failure;
    DF_ATOMIC_STORE(task->status, DF_RUN_FAILED);
  } else {
    DF_ATOMIC_STORE(task->status, DF_RUN_COMPLETED);
  }
  task->ended = true;
  df_cond_broadcast(&task->changed);
  df_mutex_unlock(&task->lock);
  df_thread_cleanup();
  df_task_release((uint64_t)(uintptr_t)task);
}

/* start(): 0 started, 1 already started (spec 20.1: a second start is an exception), 2 the system gave no thread */
static int32_t df_task_start(uint64_t handle, int64_t timeout) {
  df_task* task = (df_task*)(uintptr_t)handle;
  df_mutex_lock(&task->lock);
  if (task->status != DF_RUN_READY) {
    df_mutex_unlock(&task->lock);
    return 1;
  }
  int64_t ms = df_task_lapse_ms(timeout);
  if (ms >= 0) {
    task->timeout = ms;
  }
  DF_ATOMIC_STORE(task->status, DF_RUN_RUNNING);
  df_cond_broadcast(&task->changed);
  df_mutex_unlock(&task->lock);
  DF_ATOMIC_ADD(task->refs, 1);   /* the thread's */
  df_thread thread;
  if (!df_thread_start(&thread, df_task_main, task)) {
    df_mutex_lock(&task->lock);
    DF_ATOMIC_STORE(task->status, DF_RUN_FAILED);
    task->ended = true;
    df_cond_broadcast(&task->changed);
    df_mutex_unlock(&task->lock);
    DF_ATOMIC_SUB(task->refs, 1);
    return 2;
  }
  df_thread_detach(thread);
  return 0;
}

/* wait task [timeout t] (spec 20.2): the next value into out (size bytes, zero when there is none). The exception that ended the task
   is given to one waiter, as its error; a timeout, a task that ended, a task that never started leave a silent note (df_silent_desc) */
static df_exception* df_task_wait(uint64_t handle, void* out, size_t size, int64_t timeout) {
  df_task* task = (df_task*)(uintptr_t)handle;
  if (size) {
    memset(out, 0, size);
  }
  int64_t ms = df_task_lapse_ms(timeout);
  df_mutex_lock(&task->lock);
  if (ms < 0) {
    ms = task->timeout;   /* constructor < start < wait (spec 20.1) */
  }
  if (ms < 0) {
    /* no timeout from the wait, start or the constructor: a real exception (spec 20.1) */
    df_mutex_unlock(&task->lock);
    const char* text = "The wait has no timeout: give one to the wait, to start or to the constructor of the task";
    return df_new_exception(&df_desc_WaitingWithoutTimeoutException, text, strlen(text), "wait");
  }
  uint64_t ticket = task->nextTicket++;
  task->waiting++;
  int64_t until = df_mono_ms() + ms;
  const df_descriptor* note = NULL;
  df_exception* error = NULL;
  for (;;) {
    df_task_skip_given(task);
    if (ticket == task->serving) {
      if (task->full) {
        memcpy(out, task->slot, size < task->size ? size : task->size);
        task->full = false;
        break;
      }
      if (task->ended) {
        if (task->failure) {
          error = task->failure;   /* the exception that left taskRun(): this waiter gets it (spec 20.2) */
          task->failure = NULL;
        } else if (task->size == 0 && !task->endTaken) {
          task->endTaken = true;   /* a void task: this wait was for its end */
        } else {
          note = &df_desc_TaskAlreadyTerminatedException;
        }
        break;
      }
    }
    int64_t left = until - df_mono_ms();
    if (left <= 0) {
      note = task->status == DF_RUN_READY ? &df_desc_TaskWasNotStartedException : &df_desc_WaitTimeoutException;
      if (ticket != task->serving) {
        if (task->givenCount == task->givenCap) {
          task->givenCap = task->givenCap ? task->givenCap * 2 : 8;
          uint64_t* grown = (uint64_t*)realloc(task->given, task->givenCap * sizeof(uint64_t));
          if (!grown) {
            df_out_of_memory();
          }
          task->given = grown;
        }
        task->given[task->givenCount++] = ticket;   /* its turn is skipped when it comes */
        task->waiting--;
        df_cond_broadcast(&task->changed);
        df_mutex_unlock(&task->lock);
        df_silent_desc = note;
        return NULL;
      }
      break;
    }
    df_cond_wait_ms(&task->changed, &task->lock, left);
  }
  task->serving++;
  task->waiting--;
  df_cond_broadcast(&task->changed);
  df_mutex_unlock(&task->lock);
  if (note) {
    df_silent_desc = note;
  }
  return error;
}

/* if t is Task<MyTask> mt, Task<*, i32> (spec 20.9): the class of the object (or a parent), the result taskRun() gives */
static bool df_task_is(uint64_t handle, const df_class* cls, const char* result) {
  df_task* task = (df_task*)(uintptr_t)handle;
  return task != NULL && (cls == NULL || df_is(task->obj, cls)) && (result == NULL || strcmp(task->result, result) == 0);
}

static int32_t df_task_status(uint64_t handle) {
  return DF_ATOMIC_LOAD(((df_task*)(uintptr_t)handle)->status);
}

static df_string df_task_name(uint64_t handle) {
  return df_str_from_cstr(((df_task*)(uintptr_t)handle)->name);
}

static bool df_task_linked(uint64_t handle) {
  return ((df_task*)(uintptr_t)handle)->linked;
}
""";
}
