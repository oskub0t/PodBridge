/*
 * PodBridge AAP driver. See podbridgeaap.h for the contract and driver/README.md for status.
 * Structure follows Microsoft's Bluetooth Echo L2CAP profile driver sample (bthecho, client side).
 */
#include "podbridgeaap.h"

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, PodBridgeEvtDeviceAdd)
#pragma alloc_text(PAGE, PodBridgeEvtFileCreate)
#pragma alloc_text(PAGE, PodBridgeEvtFileCleanup)
#endif

/* ---------------------------------------------------------------------------------------------
 * Helpers
 * ------------------------------------------------------------------------------------------- */

/* Sends a BRB to the Bluetooth stack and waits for it. PASSIVE_LEVEL only. */
static NTSTATUS
SendBrbSync(_In_ PDEVICE_CONTEXT Ctx, _In_ PBRB Brb, _In_ ULONG BrbSize)
{
    WDF_MEMORY_DESCRIPTOR arg1;
    PAGED_CODE();
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&arg1, Brb, BrbSize);
    return WdfIoTargetSendInternalIoctlOthersSynchronously(
        Ctx->IoTarget, NULL, IOCTL_INTERNAL_BTH_SUBMIT_BRB, &arg1, NULL, NULL, NULL, NULL);
}

/* Asks BTHENUM which remote device we were loaded for (the AirPods' address). */
static NTSTATUS
QueryRemoteAddress(_In_ PDEVICE_CONTEXT Ctx)
{
    BTH_DEVICE_INFO info;
    WDF_MEMORY_DESCRIPTOR out;
    NTSTATUS status;
    PAGED_CODE();

    RtlZeroMemory(&info, sizeof(info));
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&out, &info, sizeof(info));
    status = WdfIoTargetSendInternalIoctlSynchronously(
        Ctx->IoTarget, NULL, IOCTL_INTERNAL_BTHENUM_GET_DEVINFO, NULL, &out, NULL, NULL);
    if (NT_SUCCESS(status)) {
        Ctx->RemoteAddress = info.address;
        PBLOG("remote address %012I64X", Ctx->RemoteAddress);
    } else {
        PBLOG("GET_DEVINFO failed 0x%08X", status);
    }
    return status;
}

static NTSTATUS
OpenChannel(_In_ PDEVICE_CONTEXT Ctx)
{
    struct _BRB_L2CA_OPEN_CHANNEL *brb;
    NTSTATUS status;
    PAGED_CODE();

    if (Ctx->RemoteAddress == 0) {
        status = QueryRemoteAddress(Ctx);
        if (!NT_SUCCESS(status)) return status;
    }

    brb = (struct _BRB_L2CA_OPEN_CHANNEL *)
        Ctx->Profile.BthAllocateBrb(BRB_L2CA_OPEN_CHANNEL, POOLTAG_PODBRIDGE);
    if (brb == NULL) return STATUS_INSUFFICIENT_RESOURCES;

    brb->BtAddress = Ctx->RemoteAddress;
    brb->Psm = AAP_PSM;
    brb->ChannelFlags = CF_ROLE_EITHER;
    brb->ConfigOut.Flags = 0;
    brb->ConfigOut.Mtu.Max = L2CAP_DEFAULT_MTU;
    brb->ConfigOut.Mtu.Min = L2CAP_MIN_MTU;
    brb->ConfigOut.Mtu.Preferred = L2CAP_DEFAULT_MTU;
    brb->ConfigIn.Flags = 0;
    brb->ConfigIn.Mtu.Max = L2CAP_DEFAULT_MTU;
    brb->ConfigIn.Mtu.Min = L2CAP_MIN_MTU;
    brb->ConfigIn.Mtu.Preferred = L2CAP_DEFAULT_MTU;
    brb->IncomingQueueDepth = 50;
    brb->ReferenceObject = (PVOID)WdfDeviceWdmGetDeviceObject(Ctx->Device);
    brb->CallbackFlags = CALLBACK_DISCONNECT;
    brb->Callback = PodBridgeIndication;
    brb->CallbackContext = Ctx;

    status = SendBrbSync(Ctx, (PBRB)brb, sizeof(*brb));
    if (NT_SUCCESS(status)) {
        Ctx->ChannelHandle = brb->ChannelHandle;
        InterlockedExchange(&Ctx->RemoteDisconnected, 0);
        PBLOG("AAP channel open");
    } else {
        PBLOG("open channel failed 0x%08X", status);
    }
    Ctx->Profile.BthFreeBrb((PBRB)brb);
    return status;
}

static VOID
CloseChannel(_In_ PDEVICE_CONTEXT Ctx)
{
    struct _BRB_L2CA_CLOSE_CHANNEL *brb;
    NTSTATUS status;
    PAGED_CODE();

    if (Ctx->ChannelHandle == NULL) return;
    brb = (struct _BRB_L2CA_CLOSE_CHANNEL *)
        Ctx->Profile.BthAllocateBrb(BRB_L2CA_CLOSE_CHANNEL, POOLTAG_PODBRIDGE);
    if (brb != NULL) {
        brb->BtAddress = Ctx->RemoteAddress;
        brb->ChannelHandle = Ctx->ChannelHandle;
        status = SendBrbSync(Ctx, (PBRB)brb, sizeof(*brb));
        PBLOG("close channel 0x%08X", status);
        Ctx->Profile.BthFreeBrb((PBRB)brb);
    }
    Ctx->ChannelHandle = NULL;
}

/* Wraps an app read/write request in an ACL transfer BRB and sends it down asynchronously. */
static VOID
SendTransfer(_In_ PDEVICE_CONTEXT Ctx, _In_ WDFREQUEST Request, _In_ BOOLEAN In, _In_ size_t Length)
{
    struct _BRB_L2CA_ACL_TRANSFER *brb = NULL;
    WDF_OBJECT_ATTRIBUTES attrs;
    WDFMEMORY brbMemory = NULL;
    PVOID buffer = NULL;
    NTSTATUS status;

    if (Ctx->ChannelHandle == NULL || Ctx->ChannelOwner != WdfRequestGetFileObject(Request)) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_STATE);
        return;
    }
    if (InterlockedCompareExchange(&Ctx->RemoteDisconnected, 0, 0) != 0) {
        WdfRequestComplete(Request, STATUS_DEVICE_NOT_CONNECTED);
        return;
    }
    if (Length == 0 || Length > MAXULONG) {
        WdfRequestComplete(Request, STATUS_INVALID_BUFFER_SIZE);
        return;
    }

    status = In ? WdfRequestRetrieveOutputBuffer(Request, Length, &buffer, NULL)
                : WdfRequestRetrieveInputBuffer(Request, Length, &buffer, NULL);
    if (!NT_SUCCESS(status)) goto fail;

    brb = (struct _BRB_L2CA_ACL_TRANSFER *)
        Ctx->Profile.BthAllocateBrb(BRB_L2CA_ACL_TRANSFER, POOLTAG_PODBRIDGE);
    if (brb == NULL) { status = STATUS_INSUFFICIENT_RESOURCES; goto fail; }

    brb->BtAddress = Ctx->RemoteAddress;
    brb->ChannelHandle = Ctx->ChannelHandle;
    brb->TransferFlags = In ? (ACL_TRANSFER_DIRECTION_IN | ACL_SHORT_TRANSFER_OK)
                            : ACL_TRANSFER_DIRECTION_OUT;
    brb->Buffer = buffer;
    brb->BufferMDL = NULL;
    brb->BufferSize = (ULONG)Length;
    brb->Timeout = 0;   /* reads wait until the AirPods send something */

    WDF_OBJECT_ATTRIBUTES_INIT(&attrs);
    attrs.ParentObject = Request;   /* freed with the request */
    status = WdfMemoryCreatePreallocated(&attrs, brb, sizeof(*brb), &brbMemory);
    if (!NT_SUCCESS(status)) goto fail;

    status = WdfIoTargetFormatRequestForInternalIoctlOthers(
        Ctx->IoTarget, Request, IOCTL_INTERNAL_BTH_SUBMIT_BRB,
        brbMemory, NULL, NULL, NULL, NULL, NULL);
    if (!NT_SUCCESS(status)) goto fail;

    WdfRequestSetCompletionRoutine(Request, PodBridgeTransferComplete, brb);
    if (!WdfRequestSend(Request, Ctx->IoTarget, WDF_NO_SEND_OPTIONS)) {
        status = WdfRequestGetStatus(Request);
        goto fail;
    }
    return;

fail:
    PBLOG("%s transfer failed 0x%08X", In ? "read" : "write", status);
    if (brb != NULL) Ctx->Profile.BthFreeBrb((PBRB)brb);
    WdfRequestComplete(Request, status);
}

/* ---------------------------------------------------------------------------------------------
 * Callbacks
 * ------------------------------------------------------------------------------------------- */

NTSTATUS
DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, PodBridgeEvtDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS
PodBridgeEvtDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_OBJECT_ATTRIBUTES attrs;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDFDEVICE device;
    PDEVICE_CONTEXT ctx;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Driver);
    PAGED_CODE();

    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, PodBridgeEvtFileCreate, WDF_NO_EVENT_CALLBACK, PodBridgeEvtFileCleanup);
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attrs, DEVICE_CONTEXT);
    attrs.EvtCleanupCallback = PodBridgeEvtDeviceCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attrs, &device);
    if (!NT_SUCCESS(status)) return status;

    ctx = GetDeviceContext(device);
    ctx->Device = device;
    ctx->IoTarget = WdfDeviceGetIoTarget(device);

    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->ChannelLock);
    if (!NT_SUCCESS(status)) return status;

    status = WdfFdoQueryForInterface(device, &GUID_BTHDDI_PROFILE_DRIVER_INTERFACE,
        (PINTERFACE)&ctx->Profile, sizeof(ctx->Profile),
        BTHDDI_PROFILE_DRIVER_INTERFACE_VERSION_FOR_QI, NULL);
    if (!NT_SUCCESS(status)) {
        PBLOG("profile driver interface unavailable 0x%08X", status);
        return status;
    }
    ctx->ProfileAcquired = TRUE;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoRead = PodBridgeEvtIoRead;
    queueConfig.EvtIoWrite = PodBridgeEvtIoWrite;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) return status;

    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_PODBRIDGE_AAP, NULL);
    PBLOG("device added 0x%08X", status);
    return status;
}

VOID
PodBridgeEvtDeviceCleanup(_In_ WDFOBJECT Object)
{
    PDEVICE_CONTEXT ctx = GetDeviceContext((WDFDEVICE)Object);
    if (ctx->ProfileAcquired && ctx->Profile.Interface.InterfaceDereference != NULL) {
        ctx->Profile.Interface.InterfaceDereference(ctx->Profile.Interface.Context);
        ctx->ProfileAcquired = FALSE;
    }
}

VOID
PodBridgeEvtFileCreate(_In_ WDFDEVICE Device, _In_ WDFREQUEST Request, _In_ WDFFILEOBJECT FileObject)
{
    PDEVICE_CONTEXT ctx = GetDeviceContext(Device);
    NTSTATUS status;
    PAGED_CODE();

    WdfWaitLockAcquire(ctx->ChannelLock, NULL);
    if (ctx->ChannelOwner != NULL) {
        status = STATUS_SHARING_VIOLATION;   /* one PodBridge at a time */
    } else {
        status = OpenChannel(ctx);
        if (NT_SUCCESS(status)) ctx->ChannelOwner = FileObject;
    }
    WdfWaitLockRelease(ctx->ChannelLock);
    WdfRequestComplete(Request, status);
}

VOID
PodBridgeEvtFileCleanup(_In_ WDFFILEOBJECT FileObject)
{
    PDEVICE_CONTEXT ctx = GetDeviceContext(WdfFileObjectGetDevice(FileObject));
    PAGED_CODE();

    WdfWaitLockAcquire(ctx->ChannelLock, NULL);
    if (ctx->ChannelOwner == FileObject) {
        CloseChannel(ctx);   /* fails any transfers still pending */
        ctx->ChannelOwner = NULL;
    }
    WdfWaitLockRelease(ctx->ChannelLock);
}

VOID
PodBridgeEvtIoRead(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request, _In_ size_t Length)
{
    SendTransfer(GetDeviceContext(WdfIoQueueGetDevice(Queue)), Request, TRUE, Length);
}

VOID
PodBridgeEvtIoWrite(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request, _In_ size_t Length)
{
    SendTransfer(GetDeviceContext(WdfIoQueueGetDevice(Queue)), Request, FALSE, Length);
}

VOID
PodBridgeTransferComplete(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target,
    _In_ PWDF_REQUEST_COMPLETION_PARAMS Params,
    _In_ WDFCONTEXT Context)
{
    struct _BRB_L2CA_ACL_TRANSFER *brb = (struct _BRB_L2CA_ACL_TRANSFER *)Context;
    PDEVICE_CONTEXT ctx = GetDeviceContext(WdfIoTargetGetDevice(Target));
    NTSTATUS status = Params->IoStatus.Status;
    /* VERIFY on first run: BufferSize is expected to hold the bytes actually transferred. */
    ULONG_PTR bytes = NT_SUCCESS(status) ? brb->BufferSize : 0;

    ctx->Profile.BthFreeBrb((PBRB)brb);
    WdfRequestCompleteWithInformation(Request, status, bytes);
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
PodBridgeIndication(
    _In_ PVOID Context,
    _In_ INDICATION_CODE Indication,
    _In_ PINDICATION_PARAMETERS Parameters)
{
    PDEVICE_CONTEXT ctx = (PDEVICE_CONTEXT)Context;
    UNREFERENCED_PARAMETER(Parameters);

    if (Indication == IndicationRemoteDisconnect) {
        /* The app sees its pending read fail and closes the handle; cleanup closes the channel. */
        InterlockedExchange(&ctx->RemoteDisconnected, 1);
        PBLOG("remote disconnect");
    }
}
