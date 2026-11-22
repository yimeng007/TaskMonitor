using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace task_monitor
{
    /// <summary>
    /// P/Invoke for the per-disk metric APIs — the same fully documented Win32 path Task
    /// Manager's <c>WdcDiskMonitor</c> uses (reversed from Taskmgr.exe, disk.cpp):
    /// a zero-access <c>CreateFile</c> handle per physical disk, then
    /// <c>IOCTL_DISK_PERFORMANCE</c> every tick for the cumulative counters and
    /// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> once at enumeration for the bus type +
    /// seek-penalty (the SSD test). All control codes are FILE_ANY_ACCESS, so none of
    /// this needs elevation. Only used by <c>DiskSampler</c>.
    /// </summary>
    internal static class DiskInterop
    {
        // CTL_CODE(IOCTL_DISK_BASE=0x07, 0x020, METHOD_BUFFERED, FILE_ANY_ACCESS).
        // Returns DISK_PERFORMANCE (0x58 bytes) — cumulative, kernel-maintained counters.
        internal const uint IOCTL_DISK_PERFORMANCE = 0x00070020;
        // CTL_CODE(IOCTL_STORAGE_BASE=0x2D, 0x500, METHOD_BUFFERED, FILE_ANY_ACCESS).
        internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        // CTL_CODE(IOCTL_VOLUME_BASE=0x56, 0, METHOD_BUFFERED, FILE_ANY_ACCESS).
        // On a volume handle (\\.\C:) returns the disk extent(s) — the volume→physical-disk
        // mapping behind the "磁盘 0 (C: D:)" tab titles (Taskmgr's GetDiskExtents).
        internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

        internal const int StorageDeviceProperty = 0;           // → STORAGE_DEVICE_DESCRIPTOR (BusType, vendor/product strings)
        internal const int StorageDeviceSeekPenaltyProperty = 7; // → DEVICE_SEEK_PENALTY_DESCRIPTOR (the SSD test, Win8+)
        // ⚠ STORAGE_PROPERTY_ID 在 0x0F 之后**跳号**（StorageDeviceIoCapabilityProperty = 48 起），
        //   按顺序数出来的值全错（0x16 当温度 → DeviceIoControl 返回 err=1）。下面都是真实值。
        internal const int StorageDeviceTemperatureProperty = 0x34;          // → STORAGE_TEMPERATURE_DATA_DESCRIPTOR
        internal const uint GenericRead = 0x80000000;                        // 温度查询要读权限（管理员）
        internal const uint GenericWrite = 0x40000000;                       // NVMe SMART 要读写句柄
        internal const int StorageAdapterProtocolSpecificProperty = 0x31;    // NVMe SMART（主）
        internal const int StorageDeviceProtocolSpecificProperty = 0x32;     // NVMe SMART（备）
        internal const int ProtocolTypeNvme = 3;
        internal const int NvmeDataTypeLogPage = 2;
        internal const int NvmeSmartLogId = 2;                               // SMART/Health Information
        // 头部 = TStoragePropertyQuery(8, 只有 PropertyId+QueryType) + TStorageProtocolSpecificData(40) = 48。
        // 套 Win32 那个带 AdditionalParameters[1] 的 12 字节版本会整块错位 → err=1（2026-11-22 踩过）。
        internal const int ProtocolSpecificHeaderSize = 48;
        // 头部内各字段偏移（与 CDI StorageQuery.h 一致）
        internal const int OffsetPropertyId = 0;
        internal const int OffsetQueryType = 4;
        internal const int OffsetProtocolType = 8;
        internal const int OffsetDataType = 12;
        internal const int OffsetRequestValue = 16;
        internal const int OffsetRequestSubValue = 20;
        internal const int OffsetDataOffset = 24;
        internal const int OffsetDataLength = 28;
        internal const int NvmeSmartLogLength = 4096;            // 请求长度（CDI 同值）
        internal const int PropertyStandardQuery = 0;

        // STORAGE_BUS_TYPE values DiskSampler switches on (ntddstor.h).
        internal const int BusTypeUsb = 7;
        internal const int BusTypeSd = 0x0C;
        internal const int BusTypeFileBackedVirtual = 0x0F; // VHD/VHDX — excluded, same as Task Manager
        internal const int BusTypeScm = 0x12;               // Storage Class Memory (persistent memory)

        // CreateFile flags for the zero-access open (metadata only — no read/write rights,
        // which is why no elevation is needed for the FILE_ANY_ACCESS IOCTLs above).
        internal const uint FILE_SHARE_READ_WRITE = 0x3;
        internal const uint OPEN_EXISTING = 3;

        // winioctl.h DISK_PERFORMANCE (x64 layout, 0x58 bytes). Time fields are 100ns
        // units at system-tick resolution (~15.6 ms granularity — fine at a 1s cadence).
        [StructLayout(LayoutKind.Sequential)]
        internal struct DISK_PERFORMANCE
        {
            public long BytesRead;         // 0x00 cumulative
            public long BytesWritten;      // 0x08 cumulative
            public long ReadTime;          // 0x10 cumulative 100ns spent reading
            public long WriteTime;         // 0x18 cumulative 100ns spent writing
            public long IdleTime;          // 0x20 cumulative 100ns with an empty queue
            public int ReadCount;          // 0x28
            public int WriteCount;         // 0x2C
            public int QueueDepth;         // 0x30
            public int SplitCount;         // 0x34
            public long QueryTime;         // 0x38 sample clock (100ns) — the delta denominator
            public uint StorageDeviceNumber; // 0x40
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public ushort[] StorageManagerName; // 0x44 ("SCSI\0\0\0\0\0" etc.) — unread
        }

        // STORAGE_PROPERTY_QUERY input (0x0C bytes: two ints + the 1-byte AdditionalParameters).
        internal static byte[] MakePropertyQuery(int propertyId)
        {
            var buf = new byte[0x0C];
            BitConverter.GetBytes(propertyId).CopyTo(buf, 0);
            BitConverter.GetBytes(PropertyStandardQuery).CopyTo(buf, 4);
            return buf;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            ref DISK_PERFORMANCE lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);
    }
}
