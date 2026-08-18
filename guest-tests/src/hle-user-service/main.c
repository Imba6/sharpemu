#include <stdint.h>

int sceUserServiceInitialize(void *params);
int sceUserServiceGetInitialUser(int32_t *user_id);
int sceUserServiceTerminate(void);

static volatile int32_t g_user_sink;

int main(void)
{
    int result;

    result = sceUserServiceInitialize(0);

    if (result != 0) {
        __builtin_trap();
    }

    int32_t user_id = -1;

    result = sceUserServiceGetInitialUser(
        &user_id
    );

    if (result != 0) {
        __builtin_trap();
    }

    if (user_id != 0x10000000) {
        __builtin_trap();
    }

    g_user_sink = user_id;

    result = sceUserServiceTerminate();

    if (result != 0) {
        __builtin_trap();
    }

    return 0;
}