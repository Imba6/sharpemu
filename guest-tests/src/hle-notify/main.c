#include <stddef.h>

typedef struct notify_request {
    char reserved[45];
    char message[3075];
} notify_request_t;

int sceKernelSendNotificationRequest(
    int type,
    notify_request_t *request,
    size_t size,
    int unknown
);

int main(void)
{
    notify_request_t request;

    const char *message =
        "Hello from VirtualPS5 HLE layer!";

    size_t i = 0;

    while (message[i] != '\0') {
        request.message[i] = message[i];
        i++;
    }

    request.message[i] = '\0';

    int result = sceKernelSendNotificationRequest(
        0,
        &request,
        sizeof(request),
        0
    );

    if (result != 0) {
        __builtin_trap();
    }

    return 0;
}