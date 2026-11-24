using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// Reads DualSense / DualShock 4 touchpad coordinates directly from Windows HID (shared, read-only),
/// so touchpad cursor gliding works even when FFXIV's own controller input is blocked by Dalamud.
/// </summary>
internal sealed class TouchpadReader : IDisposable
{
    private const ushort SonyVid = 0x054C;
    private const ushort DualSensePid = 0x0CE6;
    private const ushort DualSenseEdgePid = 0x0DF2;
    private const ushort DualShock4V1Pid = 0x05C4;
    private const ushort DualShock4V2Pid = 0x09CC;

    private readonly object gate = new();
    private SafeFileHandle? handle;
    private Thread? thread;
    private volatile bool stopping;
    private float accumX;
    private float accumY;

    public bool Active => handle is { IsInvalid: false, IsClosed: false };

    /// <summary>Starts reading the first connected DualSense / DualShock 4 touchpad, if any.</summary>
    public void Start()
    {
        if (thread is not null)
            return;
        var found = OpenSonyController();
        if (found is null)
            return;
        var (h, pid, reportLen) = found.Value;
        handle = h;
        stopping = false;
        thread = new Thread(() => ReadLoop(h, pid, reportLen))
        {
            IsBackground = true,
            Name = "Hachidori Touchpad",
        };
        thread.Start();
    }

    /// <summary>Consumes accumulated touchpad movement in touchpad units (1920×1080 space).</summary>
    public Vector2 ConsumeDelta()
    {
        lock (gate)
        {
            var delta = new Vector2(accumX, accumY);
            accumX = 0;
            accumY = 0;
            return delta;
        }
    }

    public void Stop()
    {
        stopping = true;
        var h = handle;
        handle = null;
        if (h is { IsInvalid: false, IsClosed: false })
        {
            CancelIoEx(h, nint.Zero);
            h.Dispose();
        }
        var t = thread;
        thread = null;
        if (t is not null && t != Thread.CurrentThread)
            t.Join(200);
        lock (gate)
        {
            accumX = 0;
            accumY = 0;
        }
    }

    public void Dispose() => Stop();

    private void ReadLoop(SafeFileHandle h, ushort pid, int reportLen)
    {
        try
        {
            var buffer = new byte[Math.Max(64, reportLen)];
            var prevTouching = false;
            var prevId = -1;
            var prevX = 0;
            var prevY = 0;
            var isDualSense = pid is DualSensePid or DualSenseEdgePid;

            while (!stopping && !h.IsInvalid && !h.IsClosed)
            {
                if (!ReadFile(h, buffer, (uint)buffer.Length, out var read, nint.Zero) || read == 0)
                    break;

                var offset = TouchOffset(isDualSense, buffer[0], (int)read);
                if (offset < 0)
                    continue;

                var header = buffer[offset];
                var down = (header & 0x80) == 0;
                var id = header & 0x7F;
                var x = buffer[offset + 1] | ((buffer[offset + 2] & 0x0F) << 8);
                var y = (buffer[offset + 2] >> 4) | (buffer[offset + 3] << 4);

                lock (gate)
                {
                    if (down && prevTouching && id == prevId)
                    {
                        var dx = x - prevX;
                        var dy = y - prevY;
                        // Ignore huge single-packet jumps from palm/edge noise.
                        if (Math.Abs(dx) < 400 && Math.Abs(dy) < 400)
                        {
                            accumX += dx;
                            accumY += dy;
                        }
                    }
                }

                prevTouching = down;
                prevId = id;
                prevX = x;
                prevY = y;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("controller", $"Touchpad HID read stopped: {ex.Message}");
        }
    }

    private static int TouchOffset(bool isDualSense, byte reportId, int length)
    {
        var offset = (isDualSense, reportId) switch
        {
            (true, 0x01) => 33,  // DualSense USB
            (true, 0x31) => 34,  // DualSense Bluetooth
            (false, 0x01) => 35, // DualShock 4 USB
            (false, 0x11) => 37, // DualShock 4 Bluetooth
            _ => -1,
        };
        return offset >= 0 && offset + 3 < length ? offset : -1;
    }

    private static (SafeFileHandle Handle, ushort Pid, int ReportLen)? OpenSonyController()
    {
        try
        {
            HidD_GetHidGuid(out var hidGuid);
            var set = SetupDiGetClassDevs(ref hidGuid, nint.Zero, nint.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == nint.Zero || set == new nint(-1))
                return null;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint index = 0; SetupDiEnumDeviceInterfaces(set, nint.Zero, ref hidGuid, index, ref data); index++)
                {
                    SetupDiGetDeviceInterfaceDetail(set, ref data, nint.Zero, 0, out var needed, nint.Zero);
                    if (needed == 0 || needed > 4096)
                        continue;
                    var detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, nint.Zero))
                            continue;
                        var path = Marshal.PtrToStringUni(detail + 4);
                        if (string.IsNullOrEmpty(path))
                            continue;
                        var h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0, nint.Zero);
                        if (h.IsInvalid)
                        {
                            h.Dispose();
                            continue;
                        }

                        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (HidD_GetAttributes(h, ref attrs)
                            && attrs.VendorID == SonyVid
                            && attrs.ProductID is DualSensePid or DualSenseEdgePid or DualShock4V1Pid or DualShock4V2Pid)
                        {
                            var reportLen = 64;
                            if (HidD_GetPreparsedData(h, out var preparsed))
                            {
                                try
                                {
                                    if (HidP_GetCaps(preparsed, out var caps) == HIDP_STATUS_SUCCESS && caps.InputReportByteLength > 0)
                                        reportLen = caps.InputReportByteLength;
                                }
                                finally { HidD_FreePreparsedData(preparsed); }
                            }
                            return (h, attrs.ProductID, reportLen);
                        }
                        h.Dispose();
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("controller", $"Windows HID touchpad probe unavailable: {ex.Message}");
        }

        return null;
    }

    // ------------------------------------------------------------------ Win32

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out nint preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(nint preparsedData, out HIDP_CAPS capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, nint deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, nint lpOverlapped);
}
