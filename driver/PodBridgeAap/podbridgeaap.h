/*
 * PodBridge AAP driver: an OPTIONAL KMDF Bluetooth profile driver that opens the AirPods'
 * Apple Accessory Protocol channel (L2CAP PSM 0x1001) and passes packets to PodBridge.exe.
 *
 * EXPERIMENTAL. Written without being compiled or run; see driver/README.md for the build
 * and test steps. It moves bytes only: all protocol logic lives in the app (AapCodec/AapSession).
 *
 * Attaches to the "AAP Server" device node Windows creates for the AirPods' AAP service
 * (confirmed on AirPods Pro 3: BTHENUM\{74ec2172-...}_VID&0001004c_PID&2027).
 *
 * Contract with the app (docs/advanced-features.md):
 *   - device interface GUID_DEVINTERFACE_PODBRIDGE_AAP is registered for each AirPods device node
 *   - CreateFile opens the L2CAP channel (fails if the AirPods can't be reached); one handle at a time
 *   - WriteFile sends one AAP packet; ReadFile returns one received L2CAP SDU (one AAP packet)
 *   - CloseHandle closes the channel; a remote disconnect fails pending and new I/O
 */
#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <initguid.h>   /* single translation unit: instantiate the GUIDs below and in bthguid.h */
#include <bthdef.h>
#include <ntintsafe.h>
#include <bthguid.h>
#include <bthioctl.h>
#include <sdpnode.h>
#include <bthddi.h>
#include <bthsdpddi.h>
#include <bthsdpdef.h>

#define POOLTAG_PODBRIDGE 'BdoP'
#define AAP_PSM           0x1001

#define PBLOG(fmt, ...) \
    KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL, "PodBridgeAap: " fmt "\n", __VA_ARGS__))

/* {14937a53-ba6b-4b1f-ba4b-2e3ebe1c57cb}: must match AapDriverTransport.InterfaceGuid in the app. */
DEFINE_GUID(GUID_DEVINTERFACE_PODBRIDGE_AAP,
    0x14937a53, 0xba6b, 0x4b1f, 0xba, 0x4b, 0x2e, 0x3e, 0xbe, 0x1c, 0x57, 0xcb);

typedef struct _DEVICE_CONTEXT {
    WDFDEVICE                    Device;
    WDFIOTARGET                  IoTarget;          /* BTHENUM PDO below us */
    BTH_PROFILE_DRIVER_INTERFACE Profile;           /* BRB allocation helpers from bthport */
    BOOLEAN                      ProfileAcquired;
    BTH_ADDR                     RemoteAddress;     /* the AirPods' Bluetooth address */
    WDFWAITLOCK                  ChannelLock;       /* guards the fields below */
    WDFFILEOBJECT                ChannelOwner;      /* the open handle that owns the channel */
    L2CAP_CHANNEL_HANDLE         ChannelHandle;
    volatile LONG                RemoteDisconnected;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD PodBridgeEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP PodBridgeEvtDeviceCleanup;
EVT_WDF_DEVICE_FILE_CREATE PodBridgeEvtFileCreate;
EVT_WDF_FILE_CLEANUP PodBridgeEvtFileCleanup;
EVT_WDF_IO_QUEUE_IO_READ PodBridgeEvtIoRead;
EVT_WDF_IO_QUEUE_IO_WRITE PodBridgeEvtIoWrite;
EVT_WDF_REQUEST_COMPLETION_ROUTINE PodBridgeTransferComplete;

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID PodBridgeIndication(
    _In_ PVOID Context,
    _In_ INDICATION_CODE Indication,
    _In_ PINDICATION_PARAMETERS Parameters);
