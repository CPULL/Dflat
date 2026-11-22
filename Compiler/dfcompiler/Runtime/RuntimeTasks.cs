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
  bool started;        /* the task took it from its queue */
  bool lends;          /* it lends values of the caller (plain or &, spec 20.12): the caller waits for its end once it has started */
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
  /* signals sent to this runnable (spec 20.3.1), under the lock of the signals */
  struct df_sig_count* sigCounts;     /* Stable: the sends kept for this runnable, by signal */
  size_t sigCountN;
  size_t sigCountCap;
  int32_t cancelled;                  /* the cancel signal arrived (KnownSignals.Cancel, spec 20.10): it ends at its next checkpoint */
} df_task;

/* the task this thread runs; NULL in the main program */
static DF_TLS df_task* df_task_current = NULL;

/* every runnable that exists (spec 20.10: the soft kill reaches the descendants), under its own lock */
#ifdef _WIN32
static df_mutex df_task_all_lock = SRWLOCK_INIT;
#else
static df_mutex df_task_all_lock = PTHREAD_MUTEX_INITIALIZER;
#endif
static df_task** df_task_all = NULL;
/* the lock and the condition of the signals (spec 20.3), also used by the cancel signal */
#ifdef _WIN32
static df_mutex df_sig_lock = SRWLOCK_INIT;
static df_cond df_sig_changed = CONDITION_VARIABLE_INIT;
#else
static df_mutex df_sig_lock = PTHREAD_MUTEX_INITIALIZER;
static df_cond df_sig_changed = PTHREAD_COND_INITIALIZER;
#endif

static size_t df_task_all_n = 0;
static size_t df_task_all_cap = 0;

static void df_task_all_add(df_task* task) {
  df_mutex_lock(&df_task_all_lock);
  if (df_task_all_n == df_task_all_cap) {
    df_task_all_cap = df_task_all_cap ? df_task_all_cap * 2 : 16;
    df_task** grown = (df_task**)realloc(df_task_all, df_task_all_cap * sizeof(df_task*));
    if (!grown) {
      df_out_of_memory();
    }
    df_task_all = grown;
  }
  df_task_all[df_task_all_n++] = task;
  df_mutex_unlock(&df_task_all_lock);
}

static void df_task_all_remove(df_task* task) {
  df_mutex_lock(&df_task_all_lock);
  for (size_t i = 0; i < df_task_all_n; i++) {
    if (df_task_all[i] == task) {
      df_task_all[i] = df_task_all[--df_task_all_n];
      break;
    }
  }
  df_mutex_unlock(&df_task_all_lock);
}

/* the cancel signal of the runnable this code runs in (spec 20.10): the exception that makes it end, raised once (cancelled 1 -> 2:
   its atEnd blocks may wait again); NULL when there is none */
static df_exception* df_task_cancel_now(void) {
  df_task* task = df_task_current;
  if (task == NULL || DF_ATOMIC_LOAD(task->cancelled) != 1) {
    return NULL;
  }
  DF_ATOMIC_STORE(task->cancelled, 2);
  const char* text = "The runnable was cancelled (spec 20.10)";
  return df_new_exception(&df_desc_Cancelled, text, strlen(text), "kill");
}

/* a wait of a runnable looks at its cancel signal at least this often (milliseconds) */
#define DF_CANCEL_SLICE 20
static int64_t df_task_slice(int64_t left) {
  if (df_task_current == NULL) {
    return left;
  }
  return left < 0 || left > DF_CANCEL_SLICE ? DF_CANCEL_SLICE : left;
}

static void df_task_release(uint64_t handle);

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
  if (task->parent) {
    DF_ATOMIC_ADD(task->parent->refs, 1);   /* the child keeps its parent known (spec 20.10) */
  }
  df_mutex_init(&task->lock);
  df_cond_init(&task->changed);
  task->size = size;
  task->drop = drop;
  task->slot = size ? df_alloc(size) : NULL;
  df_task_all_add(task);
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
  df_task_all_remove(task);
  df_task* parent = task->parent;
  free(task->sigCounts);
  free(task->slot);
  free(task->given);
  free(task);
  if (parent) {
    df_task_release((uint64_t)(uintptr_t)parent);
  }
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
      call->started = true;
    }
    df_mutex_unlock(&task->lock);
    if (call == NULL) {
      return;
    }
    void* serving = df_serving;
    df_serving = NULL;   /* the waits of a call being served do their own job only: calls never nest (spec 20.1) */
    call->invoke(task->obj, call);
    df_serving = serving;
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
    df_exception* cancel = df_task_cancel_now();
    if (cancel) {
      if (df_pending) {
        df_exc_release(df_pending);
      }
      df_pending = cancel;   /* the runnable starts ending (spec 20.10) */
    }
  }
}

/* wait 250 in taskRun() (spec 20.8): the time passes, and the calls that come meanwhile are served; elsewhere a plain sleep */
static void df_task_pause(int64_t lapse, void* self) {
  df_task* task = self ? df_task_of(self) : NULL;
  if (task == NULL && df_task_current == NULL) {
    df_time_sleep(lapse);
    return;
  }
  int64_t ms = df_task_lapse_ms(lapse);
  int64_t until = df_mono_ms() + (ms < 0 ? 0 : ms);
  df_task* own = task ? task : df_task_current;   /* a thread, or a function called by a task: no calls served */
  for (;;) {
    if (task) {
      df_task_serve_calls(task);
    }
    df_exception* cancel = df_task_cancel_now();
    if (cancel) {
      df_pending = cancel;   /* a wait is a checkpoint (spec 20.8, 20.10) */
      return;
    }
    int64_t left = until - df_mono_ms();
    if (left <= 0) {
      break;
    }
    df_mutex_lock(&own->lock);
    if (own->callsHead == NULL || task == NULL) {
      df_cond_wait_ms(&own->changed, &own->lock, df_task_slice(left));
    }
    df_mutex_unlock(&own->lock);
  }
  if (task) {
    df_task_raise_posted(task);
  }
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
  df_exception* cancelled = NULL;   /* the cancel signal that came while a call with lends ran */
  bool late = false;                /* the timeout ran out while a call with lends ran */
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
    df_exception* cancel = cancelled ? NULL : df_task_cancel_now();
    int64_t left = until - df_mono_ms();
    if ((cancel || left <= 0) && call->lends) {
      if (!call->started) {
        /* lends (spec 20.12): a call that has not started is withdrawn, the task never sees the values */
        df_task_call* before = NULL;
        for (df_task_call* at = task->callsHead; at; before = at, at = at->next) {
          if (at == call) {
            if (before) {
              before->next = at->next;
            } else {
              task->callsHead = at->next;
            }
            if (task->callsTail == at) {
              task->callsTail = before;
            }
            break;
          }
        }
        DF_ATOMIC_SUB(call->refs, 1);   /* the reference of the queue */
        if (cancel) {
          error = cancel;
        } else {
          df_silent_desc = &df_desc_WaitTimeoutException;
        }
        break;
      }
      /* it runs on the values of the caller: the caller waits for its end (temporary, spec 20.12) */
      if (cancel) {
        cancelled = cancel;
      }
      late = late || left <= 0;
      left = DF_CANCEL_SLICE;
    } else if (cancel) {
      error = cancel;
      break;
    } else if (left <= 0) {
      df_silent_desc = &df_desc_WaitTimeoutException;
      break;
    }
    if (df_serving != NULL && (df_task*)df_serving != task) {
      df_mutex_unlock(&task->lock);   /* a wait in taskRun(): the calls through the handle meanwhile (spec 20.8) */
      df_task_serve_calls((df_task*)df_serving);
      df_mutex_lock(&task->lock);
      continue;
    }
    df_cond_wait_ms(&task->changed, &task->lock, df_task_slice(left));
  }
  df_mutex_unlock(&task->lock);
  if (cancelled) {
    if (error) {
      df_exc_release(error);
    }
    return cancelled;   /* the runnable ends now that the call is over */
  }
  (void)late;
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
      bool cancelled = DF_ATOMIC_LOAD(task->cancelled) == 1 && task == df_task_current;
      if (ms >= 0 && left <= 0 || cancelled) {
        task->full = false;   /* nobody collected it in time, or the runnable is cancelled: dropped (spec 20.1, 20.10) */
        if (task->drop) {
          task->drop(task->slot);
        }
        break;
      }
      df_cond_wait_ms(&task->changed, &task->lock, df_task_slice(left));
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
  df_exception* cancel = df_task_cancel_now();
  if (cancel) {
    if (df_pending) {
      df_exc_release(df_pending);
    }
    df_pending = cancel;   /* a return is a checkpoint (spec 20.8, 20.10) */
  }
  return true;
}

/* return! and return? in the function of a thread: only while it runs as that thread; false otherwise (a direct call) */
static bool df_thread_give_from(void* fn, void* value, int mode) {
  df_task* task = df_task_current;
  if (task == NULL || task->threadFn != fn) {
    return false;
  }
  df_task_give(task, value, mode);
  df_exception* cancel = df_task_cancel_now();
  if (cancel) {
    df_pending = cancel;   /* a return is a checkpoint (spec 20.8, 20.10) */
  }
  return true;
}

/* the cancel signal for one runnable (spec 20.10): kept until its next checkpoint; a runnable that never started ends now, Killed */
static void df_task_send_cancel(df_task* task) {
  df_mutex_lock(&df_sig_lock);
  if (DF_ATOMIC_LOAD(task->cancelled) == 0) {
    DF_ATOMIC_STORE(task->cancelled, 1);
  }
  df_cond_broadcast(&df_sig_changed);
  df_mutex_unlock(&df_sig_lock);
  df_mutex_lock(&task->lock);
  if (task->status == DF_RUN_READY) {
    DF_ATOMIC_STORE(task->status, DF_RUN_KILLED);
    task->ended = true;
  }
  df_cond_broadcast(&task->changed);
  df_mutex_unlock(&task->lock);
}

static bool df_task_has_ended(df_task* task) {
  df_mutex_lock(&task->lock);
  bool ended = task->ended;
  df_mutex_unlock(&task->lock);
  return ended;
}

/* a runnable that ends: its threads and its linked tasks get the soft kill, and it does not wait for them (spec 20.1, 20.10) */
static void df_task_cancel_children(df_task* task) {
  df_mutex_lock(&df_task_all_lock);
  size_t n = 0;
  df_task** children = (df_task**)malloc((df_task_all_n ? df_task_all_n : 1) * sizeof(df_task*));
  if (!children) {
    df_out_of_memory();
  }
  for (size_t i = 0; i < df_task_all_n; i++) {
    if (df_task_all[i]->parent == task) {
      DF_ATOMIC_ADD(df_task_all[i]->refs, 1);
      children[n++] = df_task_all[i];
    }
  }
  df_mutex_unlock(&df_task_all_lock);
  for (size_t i = 0; i < n; i++) {
    if (!df_task_has_ended(children[i])) {
      df_task_send_cancel(children[i]);
    }
    df_task_release((uint64_t)(uintptr_t)children[i]);
  }
  free(children);
}

/* the distance from the runnable to of, going up its parents; -1 when of is not above it (NULL: the main program, above everything) */
static int64_t df_task_depth_below(df_task* task, df_task* of) {
  int64_t depth = 0;
  for (df_task* at = task; at; at = at->parent) {
    if (at == of) {
      return depth;
    }
    depth++;
  }
  return of == NULL ? depth : -1;
}

/* the soft kill of the runnables in list (levels: their distance from the top), deepest level first, each level in its own window;
   true when all of them ended before the deadline (spec 20.10) */
static bool df_task_cancel_levels(df_task** list, int64_t* levels, size_t n, int64_t ms, bool oneWindow) {
  int64_t deepest = 0;
  for (size_t i = 0; i < n; i++) {
    if (levels[i] > deepest) {
      deepest = levels[i];
    }
  }
  int64_t start = df_mono_ms();
  int64_t deadline = start + ms;
  int64_t window = ms / (deepest + 1);
  for (int64_t level = oneWindow ? -1 : deepest; level >= (oneWindow ? -1 : 0); level--) {
    for (size_t i = 0; i < n; i++) {
      if ((oneWindow || levels[i] == level) && !df_task_has_ended(list[i])) {
        df_task_send_cancel(list[i]);
      }
    }
    int64_t levelEnd = oneWindow ? deadline : df_mono_ms() + window;
    if (levelEnd > deadline) {
      levelEnd = deadline;
    }
    for (;;) {
      bool all = true;
      for (size_t i = 0; i < n; i++) {
        if ((oneWindow || levels[i] == level) && !df_task_has_ended(list[i])) {
          all = false;
          break;
        }
      }
      if (all || df_mono_ms() >= levelEnd) {
        break;
      }
      df_time_sleep((2 << 3) | 2);   /* 2 ms */
    }
  }
  bool ended = true;
  for (size_t i = 0; i < n; i++) {
    if (!df_task_has_ended(list[i])) {
      ended = false;
    }
  }
  return ended;
}

/* the runnables at or below top (top NULL: all of them), retained, with their distance from top */
static size_t df_task_subtree(df_task* top, df_task*** list, int64_t** levels) {
  df_mutex_lock(&df_task_all_lock);
  *list = (df_task**)malloc((df_task_all_n + 1) * sizeof(df_task*));
  *levels = (int64_t*)malloc((df_task_all_n + 1) * sizeof(int64_t));
  if (!*list || !*levels) {
    df_out_of_memory();
  }
  size_t n = 0;
  for (size_t i = 0; i < df_task_all_n; i++) {
    int64_t depth = df_task_depth_below(df_task_all[i], top);
    if (depth >= 0) {
      DF_ATOMIC_ADD(df_task_all[i]->refs, 1);
      (*list)[n] = df_task_all[i];
      (*levels)[n++] = depth;
    }
  }
  df_mutex_unlock(&df_task_all_lock);
  return n;
}

/* t.kill(timeout) (spec 20.10): the soft kill of t and of its descendants, level by level from the deepest, each level in a window of
   T / N; true when they all ended within the timeout. Only the descendants of the runnable that calls it: false otherwise. It never
   escalates: what has not ended keeps running */
static bool df_task_kill(uint64_t handle, int64_t timeout) {
  df_task* target = (df_task*)(uintptr_t)handle;
  df_task* me = df_task_current;
  if (target == me || df_task_depth_below(target, me) <= 0) {
    return false;
  }
  int64_t ms = df_task_lapse_ms(timeout);
  if (ms < 0) {
    ms = 0;
  }
  df_task** list;
  int64_t* levels;
  size_t n = df_task_subtree(target, &list, &levels);
  bool ended = df_task_cancel_levels(list, levels, n, ms, false);
  for (size_t i = 0; i < n; i++) {
    df_task_release((uint64_t)(uintptr_t)list[i]);
  }
  free(list);
  free(levels);
  return ended;
}

/* the end of the main program (spec 20.10): every runnable that has not ended gets the soft kill, all at once, in one window of 50 ms
   per level of the tree; then the program goes on to its exit (the hard kill is not built: what is left ends with the process) */
static void df_tasks_end_main(void) {
  df_task** list;
  int64_t* levels;
  size_t n = df_task_subtree(NULL, &list, &levels);
  int64_t deepest = 0;
  size_t running = 0;
  for (size_t i = 0; i < n; i++) {
    if (levels[i] > deepest) {
      deepest = levels[i];
    }
    if (!df_task_has_ended(list[i])) {
      running++;
    }
  }
  if (running > 0) {
    df_task_cancel_levels(list, levels, n, 50 * deepest, true);
  }
  for (size_t i = 0; i < n; i++) {
    df_task_release((uint64_t)(uintptr_t)list[i]);
  }
  free(list);
  free(levels);
}

/* the waits for a busy lock in taskRun() serve the calls through the handle (spec 20.8) */
static void df_task_serve_hook(void* task) {
  df_task_serve_calls((df_task*)task);
}

static void df_task_main(void* p) {
  df_task* task = (df_task*)p;
  df_task_current = task;
  df_cancel_flag = &task->cancelled;
  df_serve_hook = df_task_serve_hook;
  df_exception* failure = task->run(task->obj);   /* the last value (return val) is handed out inside run */
  df_mutex_lock(&task->lock);
  if (failure == NULL && task->postedError) {
    failure = task->postedError;   /* a posted call failed while the last value waited (spec 20.1) */
    task->postedError = NULL;
  }
  if (DF_ATOMIC_LOAD(task->cancelled) != 0) {
    if (failure) {
      df_exc_release(failure);   /* Killed in every case of kill, also when it ended by itself (spec 20.9) */
    }
    DF_ATOMIC_STORE(task->status, DF_RUN_KILLED);
  } else if (failure) {
    task->failure = failure;
    DF_ATOMIC_STORE(task->status, DF_RUN_FAILED);
  } else {
    DF_ATOMIC_STORE(task->status, DF_RUN_COMPLETED);
  }
  task->ended = true;
  df_cond_broadcast(&task->changed);
  df_mutex_unlock(&task->lock);
  df_task_cancel_children(task);
  df_thread_cleanup();
  df_task_release((uint64_t)(uintptr_t)task);
}

/* start(): 0 started, 1 already started (spec 20.1: a second start is an exception), 2 the system gave no thread, 3 a thread started
   inside a thread (spec 20.1: a thread cannot start another thread) */
static int32_t df_task_start(uint64_t handle, int64_t timeout) {
  df_task* task = (df_task*)(uintptr_t)handle;
  if (task->threadFn != NULL && df_task_current != NULL && df_task_current->threadFn != NULL) {
    return 3;
  }
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
    df_exception* cancel = df_task_cancel_now();
    int64_t left = until - df_mono_ms();
    if (left <= 0 || cancel) {
      note = cancel ? NULL : task->status == DF_RUN_READY ? &df_desc_TaskWasNotStartedException : &df_desc_WaitTimeoutException;
      error = cancel;   /* a wait is a checkpoint (spec 20.10) */
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
        if (note) {
          df_silent_desc = note;
        }
        return error;
      }
      break;
    }
    if (df_serving != NULL && (df_task*)df_serving != task) {
      df_mutex_unlock(&task->lock);   /* a wait in taskRun(): the calls through the handle meanwhile (spec 20.8) */
      df_task_serve_calls((df_task*)df_serving);
      df_mutex_lock(&task->lock);
    }
    df_cond_wait_ms(&task->changed, &task->lock, df_task_slice(left));
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

/* ---- signals (spec 20.3): one registry for the whole process, a name or a number for each signal, a mode set when it is made. A
   signal is little more than a counter: the waiters stand in a queue (FIFO), a send wakes one of them (Single, Exclusive,
   ExclusiveSafe) or all of them (Broadcast); Stable and ExclusiveSafe keep the sends nobody waited for. One lock for all of them ---- */
#define DF_SIG_BROADCAST 0
#define DF_SIG_EXCLUSIVE 1
#define DF_SIG_SINGLE 2
#define DF_SIG_STABLE 3
#define DF_SIG_EXCLUSIVE_SAFE 4

typedef struct df_sig_waiter {
  struct df_sig_waiter* next;
  df_task* owner;     /* the runnable that waits; NULL: the main program */
  bool woken;
  int64_t* groupWon;  /* a wait on a SignalGroup: the index of the member that fired first (-1: none yet); NULL for one signal */
  int64_t index;      /* ... and the index of this member */
} df_sig_waiter;

typedef struct df_signal {
  struct df_signal* next;   /* the registry */
  int32_t mode;
  bool byNumber;
  uint64_t number;
  char* name;               /* a copy of the name (byNumber: NULL) */
  size_t nameLen;
  df_sig_waiter* head;
  df_sig_waiter* tail;
  int64_t stored;           /* Stable, ExclusiveSafe: the sends that nobody waited for */
} df_signal;

static df_signal* df_sig_registry = NULL;

typedef struct df_sig_count {
  df_signal* sig;
  int64_t count;
} df_sig_count;

/* the counter of a Stable signal kept for a runnable (the lock is held); NULL when there is none and make is false */
static df_sig_count* df_sig_count_of(df_task* task, df_signal* sig, bool make) {
  for (size_t i = 0; i < task->sigCountN; i++) {
    if (task->sigCounts[i].sig == sig) {
      return &task->sigCounts[i];
    }
  }
  if (!make) {
    return NULL;
  }
  if (task->sigCountN == task->sigCountCap) {
    task->sigCountCap = task->sigCountCap ? task->sigCountCap * 2 : 4;
    df_sig_count* grown = (df_sig_count*)realloc(task->sigCounts, task->sigCountCap * sizeof(df_sig_count));
    if (!grown) {
      df_out_of_memory();
    }
    task->sigCounts = grown;
  }
  task->sigCounts[task->sigCountN] = (df_sig_count){ sig, 0 };
  return &task->sigCounts[task->sigCountN++];
}

/* a send nobody waited for, kept for this signal: the runnable's own first (t.signal(s)), then the one of the signal (the lock is held) */
static bool df_sig_take_stored(df_signal* sig) {
  if (df_task_current != NULL) {
    df_sig_count* own = df_sig_count_of(df_task_current, sig, false);
    if (own != NULL && own->count > 0) {
      own->count--;
      return true;
    }
  }
  if (sig->stored > 0) {
    sig->stored--;
    return true;
  }
  return false;
}

/* the signal of that name or number; NULL when it is not registered (the lock is held) */
static df_signal* df_sig_lookup(bool byNumber, uint64_t number, const char* name, size_t len) {
  for (df_signal* sig = df_sig_registry; sig; sig = sig->next) {
    if (sig->byNumber == byNumber && (byNumber ? sig->number == number : sig->nameLen == len && memcmp(sig->name, name, len) == 0)) {
      return sig;
    }
  }
  return NULL;
}

/* Signal(...) (spec 20.3): the signal of the registry, made when it is not there. 0: it exists with another mode; 1: a number below 4096
   that KnownSignals does not name (reserved) */
static uint64_t df_signal_get(int32_t mode, bool byNumber, uint64_t number, df_string name, bool known) {
  if (byNumber && number < 4096 && !known) {
    return 1;
  }
  df_sv text = { "", 0 };
  if (!byNumber) {
    text = df_view(name);
  }
  df_mutex_lock(&df_sig_lock);
  df_signal* sig = df_sig_lookup(byNumber, number, text.p, text.len);
  if (sig == NULL) {
    sig = (df_signal*)calloc(1, sizeof(df_signal));
    if (!sig) {
      df_out_of_memory();
    }
    sig->mode = mode;
    sig->byNumber = byNumber;
    sig->number = number;
    if (!byNumber) {
      sig->name = (char*)malloc(text.len + 1);
      if (!sig->name) {
        df_out_of_memory();
      }
      memcpy(sig->name, text.p, text.len);
      sig->name[text.len] = 0;
      sig->nameLen = text.len;
    }
    sig->next = df_sig_registry;
    df_sig_registry = sig;
  } else if (sig->mode != mode) {
    sig = NULL;   /* the same name with another mode (spec 20.3) */
  }
  df_mutex_unlock(&df_sig_lock);
  return sig ? (uint64_t)(uintptr_t)sig : 0;
}

/* Signal.tryGet(mode, name): the signal when it is registered with that mode; 0 otherwise */
static uint64_t df_signal_find(int32_t mode, bool byNumber, uint64_t number, df_string name) {
  df_sv text = { "", 0 };
  if (!byNumber) {
    text = df_view(name);
  }
  df_mutex_lock(&df_sig_lock);
  df_signal* sig = df_sig_lookup(byNumber, number, text.p, text.len);
  df_mutex_unlock(&df_sig_lock);
  return sig && sig->mode == mode ? (uint64_t)(uintptr_t)sig : 0;
}

/* the first waiter leaves the queue, woken; a member of a group that another member already woke is skipped. false: nobody was
   waiting (the lock is held) */
static bool df_sig_wake_first(df_signal* sig) {
  while (sig->head) {
    df_sig_waiter* w = sig->head;
    sig->head = w->next;
    if (sig->head == NULL) {
      sig->tail = NULL;
    }
    if (w->groupWon != NULL && *w->groupWon >= 0) {
      continue;   /* its group has already fired */
    }
    w->woken = true;
    if (w->groupWon != NULL) {
      *w->groupWon = w->index;
    }
    return true;
  }
  return false;
}

/* s.send() (spec 20.3): a Broadcast wakes every waiter, the other modes the first one; with nobody waiting a Stable or ExclusiveSafe
   signal keeps the send, the others lose it */
static void df_signal_send(uint64_t handle) {
  df_signal* sig = (df_signal*)(uintptr_t)handle;
  df_mutex_lock(&df_sig_lock);
  if (sig->mode == DF_SIG_BROADCAST) {
    while (df_sig_wake_first(sig)) {
    }
  } else if (!df_sig_wake_first(sig) && (sig->mode == DF_SIG_STABLE || sig->mode == DF_SIG_EXCLUSIVE_SAFE)) {
    sig->stored++;   /* nobody waits: kept */
  }
  df_cond_broadcast(&df_sig_changed);
  df_mutex_unlock(&df_sig_lock);
}

/* the waiter leaves the queue without being woken (its time ran out; the lock is held) */
static void df_sig_leave(df_signal* sig, df_sig_waiter* w) {
  df_sig_waiter* before = NULL;
  for (df_sig_waiter* at = sig->head; at; before = at, at = at->next) {
    if (at == w) {
      if (before) {
        before->next = at->next;
      } else {
        sig->head = at->next;
      }
      if (sig->tail == at) {
        sig->tail = before;
      }
      return;
    }
  }
}

/* wait s [timeout t] (spec 20.2, 20.3): the timeout of the wait, else the one of the signal; on timeout a silent WaitTimeoutException */
static df_exception* df_signal_wait(uint64_t handle, int64_t timeout, int64_t own) {
  df_signal* sig = (df_signal*)(uintptr_t)handle;
  int64_t ms = df_task_lapse_ms(timeout);
  if (ms < 0) {
    ms = df_task_lapse_ms(own);
  }
  if (ms < 0) {
    const char* text = "The wait has no timeout: give one to the wait or to the signal";
    return df_new_exception(&df_desc_WaitingWithoutTimeoutException, text, strlen(text), "wait");
  }
  df_mutex_lock(&df_sig_lock);
  if (df_sig_take_stored(sig)) {
    df_mutex_unlock(&df_sig_lock);   /* a send that nobody waited for (Stable, ExclusiveSafe) */
    return NULL;
  }
  if ((sig->mode == DF_SIG_EXCLUSIVE || sig->mode == DF_SIG_EXCLUSIVE_SAFE) && sig->head != NULL) {
    df_mutex_unlock(&df_sig_lock);
    const char* text = "This signal is Exclusive: it already has its listener";
    return df_new_exception(&df_desc_ExclusiveSignalBusyException, text, strlen(text), "wait");
  }
  df_sig_waiter w = { NULL, df_task_current, false, NULL, 0 };
  if (sig->tail) {
    sig->tail->next = &w;
  } else {
    sig->head = &w;
  }
  sig->tail = &w;
  int64_t until = df_mono_ms() + ms;
  while (!w.woken) {
    df_exception* cancel = df_task_cancel_now();
    if (cancel) {
      df_sig_leave(sig, &w);
      df_mutex_unlock(&df_sig_lock);
      return cancel;   /* a wait is a checkpoint (spec 20.10) */
    }
    int64_t left = until - df_mono_ms();
    if (left <= 0) {
      df_sig_leave(sig, &w);
      df_mutex_unlock(&df_sig_lock);
      df_silent_desc = &df_desc_WaitTimeoutException;
      return NULL;
    }
    if (df_serving != NULL) {
      df_mutex_unlock(&df_sig_lock);   /* a wait in taskRun(): the calls through the handle meanwhile (spec 20.8) */
      df_task_serve_calls((df_task*)df_serving);
      df_mutex_lock(&df_sig_lock);
      if (w.woken) {
        break;
      }
    }
    df_cond_wait_ms(&df_sig_changed, &df_sig_lock, df_task_slice(left));
  }
  df_mutex_unlock(&df_sig_lock);
  return NULL;
}

/* wait g [timeout t] on a SignalGroup (spec 20.3): the first member to fire wins; *won is its index, -1 on timeout (a silent
   WaitTimeoutException). The timeout of the wait, else the one of the group */
static df_exception* df_signal_wait_any(uint64_t* handles, size_t count, int64_t timeout, int64_t own, int64_t* won) {
  *won = -1;
  int64_t ms = df_task_lapse_ms(timeout);
  if (ms < 0) {
    ms = df_task_lapse_ms(own);
  }
  if (ms < 0) {
    const char* text = "The wait has no timeout: give one to the wait or to the group";
    return df_new_exception(&df_desc_WaitingWithoutTimeoutException, text, strlen(text), "wait");
  }
  df_mutex_lock(&df_sig_lock);
  for (size_t i = 0; i < count; i++) {
    df_signal* sig = (df_signal*)(uintptr_t)handles[i];
    if (df_sig_take_stored(sig)) {   /* a send nobody waited for (Stable, ExclusiveSafe) */
      *won = (int64_t)i;
      df_mutex_unlock(&df_sig_lock);
      return NULL;
    }
  }
  for (size_t i = 0; i < count; i++) {
    df_signal* sig = (df_signal*)(uintptr_t)handles[i];
    if ((sig->mode == DF_SIG_EXCLUSIVE || sig->mode == DF_SIG_EXCLUSIVE_SAFE) && sig->head != NULL) {
      df_mutex_unlock(&df_sig_lock);
      const char* text = "A signal of the group is Exclusive: it already has its listener";
      return df_new_exception(&df_desc_ExclusiveSignalBusyException, text, strlen(text), "wait");
    }
  }
  df_sig_waiter* nodes = (df_sig_waiter*)calloc(count ? count : 1, sizeof(df_sig_waiter));
  if (!nodes) {
    df_out_of_memory();
  }
  for (size_t i = 0; i < count; i++) {
    df_signal* sig = (df_signal*)(uintptr_t)handles[i];
    nodes[i].owner = df_task_current;
    nodes[i].groupWon = won;
    nodes[i].index = (int64_t)i;
    if (sig->tail) {
      sig->tail->next = &nodes[i];
    } else {
      sig->head = &nodes[i];
    }
    sig->tail = &nodes[i];
  }
  int64_t until = df_mono_ms() + ms;
  df_exception* cancel = NULL;
  while (*won < 0) {
    cancel = df_task_cancel_now();
    if (cancel) {
      break;   /* a wait is a checkpoint (spec 20.10) */
    }
    int64_t left = until - df_mono_ms();
    if (left <= 0) {
      break;
    }
    if (df_serving != NULL) {
      df_mutex_unlock(&df_sig_lock);   /* a wait in taskRun(): the calls through the handle meanwhile (spec 20.8) */
      df_task_serve_calls((df_task*)df_serving);
      df_mutex_lock(&df_sig_lock);
      if (*won >= 0) {
        break;
      }
    }
    df_cond_wait_ms(&df_sig_changed, &df_sig_lock, df_task_slice(left));
  }
  for (size_t i = 0; i < count; i++) {
    df_sig_leave((df_signal*)(uintptr_t)handles[i], &nodes[i]);   /* the members still in a queue */
  }
  df_mutex_unlock(&df_sig_lock);
  free(nodes);
  if (cancel) {
    *won = -1;
    return cancel;
  }
  if (*won < 0) {
    df_silent_desc = &df_desc_WaitTimeoutException;
  }
  return NULL;
}

/* t.signal(s) (spec 20.3.1): s for that runnable only. Broadcast, Single, Exclusive: it counts only if the runnable waits on s now
   (Broadcast: all its waits on s, the others: the first one), else it is lost; Stable: kept as a counter of the runnable when it does
   not wait; ExclusiveSafe (the cancel signal): never lost, the runnable starts ending at its next checkpoint (spec 20.10) */
static void df_signal_to(uint64_t runnable, uint64_t handle) {
  df_task* task = (df_task*)(uintptr_t)runnable;
  df_signal* sig = (df_signal*)(uintptr_t)handle;
  df_mutex_lock(&df_sig_lock);
  if (sig->mode == DF_SIG_EXCLUSIVE_SAFE) {
    DF_ATOMIC_STORE(task->cancelled, 1);
  }
  bool woke = false;
  for (df_sig_waiter* at = sig->head; at;) {
    df_sig_waiter* next = at->next;
    if (at->owner == task && (at->groupWon == NULL || *at->groupWon < 0)) {
      df_sig_leave(sig, at);
      at->woken = true;
      if (at->groupWon != NULL) {
        *at->groupWon = at->index;
      }
      woke = true;
      if (sig->mode != DF_SIG_BROADCAST) {
        break;
      }
    }
    at = next;
  }
  if (!woke && sig->mode == DF_SIG_STABLE) {
    df_sig_count_of(task, sig, true)->count++;
  }
  df_cond_broadcast(&df_sig_changed);
  df_mutex_unlock(&df_sig_lock);
  if (sig->mode == DF_SIG_EXCLUSIVE_SAFE) {
    df_mutex_lock(&task->lock);
    df_cond_broadcast(&task->changed);   /* a wait of the runnable on its own state is a checkpoint too */
    df_mutex_unlock(&task->lock);
  }
}

static int32_t df_signal_mode(uint64_t handle) {
  return ((df_signal*)(uintptr_t)handle)->mode;
}

static df_string df_signal_name(uint64_t handle) {
  df_signal* sig = (df_signal*)(uintptr_t)handle;
  return df_str_from_cstr(sig->byNumber ? "" : sig->name);
}

static uint64_t df_signal_number(uint64_t handle) {
  return ((df_signal*)(uintptr_t)handle)->number;
}

static bool df_signal_by_number(uint64_t handle) {
  return ((df_signal*)(uintptr_t)handle)->byNumber;
}

/* ---- events (spec 20.4): a registry of names, one lock for all (the one of the signals). The sender registers an event with its data (a
   struct: size, copy, drop); the receivers find it by name. A sent event is an item of the queue of its event; the waiters take the items
   one at a time, in order: a waiter has the item for its block, then it goes to the next waiter that waits for it and has not had it. It
   ends with stopPropagation() or when nobody else waits for it. Volatile: an item nobody waits for is lost; Static: it stays, up to keep
   items, then fullMode ---- */
#define DF_EV_VOLATILE 0
#define DF_EV_STATIC 1
#define DF_EVQ_DISCARD_NEWEST 0
#define DF_EVQ_DISCARD_OLDEST 1
#define DF_EVQ_BLOCK_SENDER 2

typedef struct df_ev_item {
  struct df_ev_item* next;
  void* data;              /* a copy of the struct, owned by the item */
  uint64_t sender;         /* the id of its Sender<T> field (spec 20.4.6); 0: none */
  bool busy;               /* a waiter has it now */
  bool processed;          /* setProcessed() */
  bool stopped;            /* stopPropagation() */
  void** seen;             /* the waiters that had it (the mark of their thread) */
  size_t seenN;
  size_t seenCap;
} df_ev_item;

typedef struct df_ev_waiter {
  struct df_ev_waiter* next;
  void* mark;
  bool hasFrom;
  uint64_t from;
} df_ev_waiter;

typedef struct df_event {
  struct df_event* next;    /* the registry */
  char* name;
  size_t nameLen;
  bool registered;
  const char* type;          /* the struct of the data, as written in Dflat */
  size_t size;
  void (*copy)(void* dst, const void* src);
  void (*drop)(void* data);
  int32_t mode;
  int32_t fullMode;
  int64_t keep;
  int64_t timeout;           /* milliseconds, from the registration */
  df_ev_item* head;
  df_ev_item* tail;
  int64_t count;
  df_ev_waiter* waiters;
} df_event;

/* what an Event object holds: the event of its name, the struct it was written with, its own timeout (-1: the one of the event) */
typedef struct df_evref {
  df_event* ev;
  const char* type;
  int64_t timeout;
} df_evref;

static df_event* df_ev_registry = NULL;

/* a name without a dot gets the namespace of its file (spec 20.4.1) */
static df_string df_event_full_name(df_string name, const char* ns) {
  df_sv text = df_view(name);
  if (ns[0] == 0 || memchr(text.p, '.', text.len) != NULL) {
    return df_str_dup(name);
  }
  size_t nsLen = strlen(ns);
  char* joined = (char*)malloc(nsLen + 1 + text.len + 1);
  if (!joined) {
    df_out_of_memory();
  }
  memcpy(joined, ns, nsLen);
  joined[nsLen] = '.';
  memcpy(joined + nsLen + 1, text.p, text.len);
  joined[nsLen + 1 + text.len] = 0;
  df_string made = df_str_from_cstr(joined);
  free(joined);
  return made;
}

/* the event of that name, made (not registered) when it is not there; the lock of the signals is held */
static df_event* df_ev_entry(df_string name) {
  df_sv text = df_view(name);
  for (df_event* ev = df_ev_registry; ev; ev = ev->next) {
    if (ev->nameLen == text.len && memcmp(ev->name, text.p, text.len) == 0) {
      return ev;
    }
  }
  df_event* ev = (df_event*)calloc(1, sizeof(df_event));
  if (!ev) {
    df_out_of_memory();
  }
  ev->name = (char*)malloc(text.len + 1);
  if (!ev->name) {
    df_out_of_memory();
  }
  memcpy(ev->name, text.p, text.len);
  ev->name[text.len] = 0;
  ev->nameLen = text.len;
  ev->next = df_ev_registry;
  df_ev_registry = ev;
  return ev;
}

static uint64_t df_ev_ref(df_event* ev, const char* type, int64_t timeout) {
  df_evref* ref = (df_evref*)malloc(sizeof(df_evref));
  if (!ref) {
    df_out_of_memory();
  }
  ref->ev = ev;
  ref->type = type;
  ref->timeout = timeout;
  return (uint64_t)(uintptr_t)ref;
}

/* Event.register(...): 0 when the name is registered with another struct (DuplicateEventNameWithDataMismatchException). A second
   registration with the same struct gives the same event */
static uint64_t df_event_register(df_string name, const char* type, size_t size, void (*copy)(void*, const void*), void (*drop)(void*),
    int32_t mode, int64_t timeout, int64_t keep, int32_t fullMode) {
  df_mutex_lock(&df_sig_lock);
  df_event* ev = df_ev_entry(name);
  if (ev->registered && strcmp(ev->type, type) != 0) {
    df_mutex_unlock(&df_sig_lock);
    return 0;
  }
  if (!ev->registered) {
    ev->registered = true;
    ev->type = type;
    ev->size = size;
    ev->copy = copy;
    ev->drop = drop;
    ev->mode = mode;
    ev->fullMode = fullMode;
    ev->keep = keep < 1 ? 1 : keep;
    int64_t ms = df_task_lapse_ms(timeout);
    ev->timeout = ms < 0 ? 0 : ms;
    df_cond_broadcast(&df_sig_changed);
  }
  df_mutex_unlock(&df_sig_lock);
  return df_ev_ref(ev, type, -1);
}

/* Event(name, timeout:): the event of that name; registered or not yet (spec 20.4.3) */
static uint64_t df_event_find(df_string name, const char* type, int64_t timeout) {
  df_mutex_lock(&df_sig_lock);
  df_event* ev = df_ev_entry(name);
  df_mutex_unlock(&df_sig_lock);
  return df_ev_ref(ev, type, df_task_lapse_ms(timeout));
}

static void df_event_release(uint64_t handle) {
  free((df_evref*)(uintptr_t)handle);
}

static df_string df_event_name(uint64_t handle) {
  return df_str_from_cstr(((df_evref*)(uintptr_t)handle)->ev->name);
}

/* 0: not registered (skipped), 1: ready, 2: registered with another struct (the lock is held) */
static int32_t df_ev_state(df_evref* ref) {
  if (!ref->ev->registered) {
    return 0;
  }
  return strcmp(ref->ev->type, ref->type) == 0 ? 1 : 2;
}

static bool df_ev_seen(df_ev_item* item, void* mark) {
  for (size_t i = 0; i < item->seenN; i++) {
    if (item->seen[i] == mark) {
      return true;
    }
  }
  return false;
}

/* a waiter that waits now, has not had the item, and takes its sender (the lock is held) */
static bool df_ev_someone_for(df_event* ev, df_ev_item* item) {
  for (df_ev_waiter* w = ev->waiters; w; w = w->next) {
    if (!df_ev_seen(item, w->mark) && (!w->hasFrom || w->from == item->sender)) {
      return true;
    }
  }
  return false;
}

/* the first waiter in line (FIFO) that can have the item now (the lock is held) */
static df_ev_waiter* df_ev_first_for(df_event* ev, df_ev_item* item) {
  for (df_ev_waiter* w = ev->waiters; w; w = w->next) {
    if (!df_ev_seen(item, w->mark) && (!w->hasFrom || w->from == item->sender)) {
      return w;
    }
  }
  return NULL;
}

static void df_ev_unlink(df_event* ev, df_ev_item* item) {
  df_ev_item* before = NULL;
  for (df_ev_item* at = ev->head; at; before = at, at = at->next) {
    if (at == item) {
      if (before) {
        before->next = at->next;
      } else {
        ev->head = at->next;
      }
      if (ev->tail == at) {
        ev->tail = before;
      }
      ev->count--;
      break;
    }
  }
}

static void df_ev_free(df_event* ev, df_ev_item* item) {
  ev->drop(item->data);
  free(item->data);
  free(item->seen);
  free(item);
}

/* ev.send(data) (spec 20.4): 0 not registered (skipped), 1 sent, 2 another struct, 3 discarded (a full static queue, or nobody waits
   for a volatile one) */
static int32_t df_event_send(uint64_t handle, const void* data, uint64_t sender) {
  df_evref* ref = (df_evref*)(uintptr_t)handle;
  df_mutex_lock(&df_sig_lock);
  int32_t state = df_ev_state(ref);
  if (state != 1) {
    df_mutex_unlock(&df_sig_lock);
    return state;
  }
  df_event* ev = ref->ev;
  df_ev_item probe = { NULL, NULL, sender, false, false, false, NULL, 0, 0 };
  bool waited = df_ev_someone_for(ev, &probe);
  if (!waited && ev->mode == DF_EV_VOLATILE) {
    df_mutex_unlock(&df_sig_lock);
    return 3;   /* nobody waits for it: lost */
  }
  if (!waited && ev->count >= ev->keep) {
    if (ev->fullMode == DF_EVQ_DISCARD_NEWEST) {
      df_mutex_unlock(&df_sig_lock);
      return 3;
    }
    if (ev->fullMode == DF_EVQ_DISCARD_OLDEST) {
      df_ev_item* old = ev->head;   /* the oldest leaves the queue; a waiter that has it now ends its block, then it is freed (spec 20.4.4) */
      df_ev_unlink(ev, old);
      if (old->busy) {
        old->stopped = true;
      } else {
        df_ev_free(ev, old);
      }
    } else {
      int64_t until = df_mono_ms() + ev->timeout;   /* BlockSender: the sender waits for room, up to the timeout of the event */
      while (ev->count >= ev->keep) {
        int64_t left = until - df_mono_ms();
        if (left <= 0) {
          df_mutex_unlock(&df_sig_lock);
          return 3;
        }
        df_cond_wait_ms(&df_sig_changed, &df_sig_lock, df_task_slice(left));
      }
    }
  }
  df_ev_item* item = (df_ev_item*)calloc(1, sizeof(df_ev_item));
  if (!item) {
    df_out_of_memory();
  }
  item->data = malloc(ev->size ? ev->size : 1);
  if (!item->data) {
    df_out_of_memory();
  }
  ev->copy(item->data, data);
  item->sender = sender;
  if (ev->tail) {
    ev->tail->next = item;
  } else {
    ev->head = item;
  }
  ev->tail = item;
  ev->count++;
  df_cond_broadcast(&df_sig_changed);
  df_mutex_unlock(&df_sig_lock);
  return 1;
}

static void df_ev_leave(df_event* ev, df_ev_waiter* w) {
  df_ev_waiter* before = NULL;
  for (df_ev_waiter* at = ev->waiters; at; before = at, at = at->next) {
    if (at == w) {
      if (before) {
        before->next = at->next;
      } else {
        ev->waiters = at->next;
      }
      return;
    }
  }
}

/* wait on ev [from x] as data [timeout t] (spec 20.4.7): the next item this waiter has not had; *out NULL when the event is not
   registered (skipped) or on timeout (a silent WaitTimeoutException). The timeout of the wait, else of Event(name, timeout:), else of
   the registration */
static df_exception* df_event_take(uint64_t handle, int64_t timeout, bool hasFrom, uint64_t from, df_ev_item** out) {
  df_evref* ref = (df_evref*)(uintptr_t)handle;
  *out = NULL;
  df_mutex_lock(&df_sig_lock);
  int32_t state = df_ev_state(ref);
  if (state == 0) {
    df_mutex_unlock(&df_sig_lock);
    return NULL;   /* not registered yet: skipped (spec 20.4.3) */
  }
  if (state == 2) {
    df_mutex_unlock(&df_sig_lock);
    const char* text = "This event is registered with another struct";
    return df_new_exception(&df_desc_DuplicateEventNameWithDataMismatchException, text, strlen(text), "wait");
  }
  df_event* ev = ref->ev;
  int64_t ms = df_task_lapse_ms(timeout);
  if (ms < 0) {
    ms = ref->timeout >= 0 ? ref->timeout : ev->timeout;
  }
  df_ev_waiter w = { NULL, (void*)&df_thread_mark, hasFrom, from };
  df_ev_waiter** tail = &ev->waiters;   /* in line, in the order of arrival (spec 20.4.5) */
  while (*tail) {
    tail = &(*tail)->next;
  }
  *tail = &w;
  int64_t until = df_mono_ms() + ms;
  df_exception* result = NULL;
  for (;;) {
    df_ev_item* found = NULL;
    for (df_ev_item* item = ev->head; item; item = item->next) {
      if (!item->busy && !item->stopped && df_ev_first_for(ev, item) == &w) {
        found = item;
        break;
      }
    }
    if (found) {
      found->busy = true;
      if (found->seenN == found->seenCap) {
        found->seenCap = found->seenCap ? found->seenCap * 2 : 4;
        void** grown = (void**)realloc(found->seen, found->seenCap * sizeof(void*));
        if (!grown) {
          df_out_of_memory();
        }
        found->seen = grown;
      }
      found->seen[found->seenN++] = w.mark;
      *out = found;
      break;
    }
    df_exception* cancel = df_task_cancel_now();
    if (cancel) {
      result = cancel;   /* a wait is a checkpoint (spec 20.10) */
      break;
    }
    int64_t left = until - df_mono_ms();
    if (left <= 0) {
      df_silent_desc = &df_desc_WaitTimeoutException;
      break;
    }
    if (df_serving != NULL) {
      df_mutex_unlock(&df_sig_lock);   /* a wait in taskRun(): the calls through the handle meanwhile (spec 20.8) */
      df_task_serve_calls((df_task*)df_serving);
      df_mutex_lock(&df_sig_lock);
      continue;
    }
    df_cond_wait_ms(&df_sig_changed, &df_sig_lock, df_task_slice(left));
  }
  df_ev_leave(ev, &w);
  df_mutex_unlock(&df_sig_lock);
  return result;
}

/* the end of the block of a waiter: the item goes to the next waiter, or ends (stopPropagation, or nobody else waits for it) */
static void df_event_done(uint64_t handle, df_ev_item* item) {
  df_evref* ref = (df_evref*)(uintptr_t)handle;
  df_event* ev = ref->ev;
  df_mutex_lock(&df_sig_lock);
  item->busy = false;
  if (item->stopped || !df_ev_someone_for(ev, item)) {
    df_ev_unlink(ev, item);
    df_ev_free(ev, item);
  }
  df_cond_broadcast(&df_sig_changed);
  df_mutex_unlock(&df_sig_lock);
}

/* the very end of the program: the registry is freed, with the items left in the queues. An event that a runnable still uses (one that
   the soft kill did not end) stays, to the exit of the process */
static void df_events_end(void) {
  df_mutex_lock(&df_sig_lock);
  df_event** link = &df_ev_registry;
  while (*link) {
    df_event* ev = *link;
    bool used = ev->waiters != NULL;
    for (df_ev_item* item = ev->head; item && !used; item = item->next) {
      used = item->busy;
    }
    if (used) {
      link = &ev->next;
      continue;
    }
    *link = ev->next;
    while (ev->head) {
      df_ev_item* item = ev->head;
      ev->head = item->next;
      df_ev_free(ev, item);
    }
    free(ev->name);
    free(ev);
  }
  df_mutex_unlock(&df_sig_lock);
}

/* the ids of the objects that can be a Sender<T> (spec 20.4.6): never 0, never used again */
static uint64_t df_oid_counter = 0;
static uint64_t df_oid_next(void) {
  return DF_ATOMIC_ADD(df_oid_counter, 1);
}
""";
}
