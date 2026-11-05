using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AppleCinemaBrightness;

/// <summary>
/// Independent implementation using Windows HID APIs, not the original application or its DLLs.
/// HID usage/report numbers were determined from static analysis of the supplied application.
/// All operations are local; there is no network access.
/// </summary>
internal sealed class HidDisplay : IDisposable
{
    private static readonly HashSet<ushort> SupportedProductIds = new()
    {
        0x9236, 0x9227, 0x9226, 0x9223, 0x9222, 0x921E,
        0x921C, 0x9221, 0x9219, 0x9218, 0x9217, 0x9216
    };

    private readonly SafeFileHandle handle;
    private readonly int reportLength;
    private readonly int brightnessBits;
    private readonly int minimumBrightness;
    private readonly int maximumBrightness;

    public string Name { get; }
    public ushort ProductId { get; }
    public bool SupportsAutoBrightness { get; }
    public string Path { get; }

    private HidDisplay(
        string path, SafeFileHandle openedHandle, string name, ushort productId,
        int featureLength, int bits, int min, int max, bool supportsAuto)
    {
        Path = path;
        handle = openedHandle;
        Name = name;
        ProductId = productId;
        reportLength = featureLength;
        brightnessBits = bits;
        minimumBrightness = min;
        maximumBrightness = max;
        SupportsAutoBrightness = supportsAuto;
    }

    public static List<HidDisplay> Discover()
    {
        var displays = new List<HidDisplay>();
        Native.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr infoSet = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (infoSet == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HIDデバイスの一覧を取得できません。");
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new Native.DeviceInterfaceData
                {
                    cbSize = Marshal.SizeOf<Native.DeviceInterfaceData>()
                };
                if (!Native.SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid,
                        index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break; // ERROR_NO_MORE_ITEMS
                    continue;
                }
                Native.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData,
                    IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
                if (requiredSize < 6 || requiredSize > 65536) continue;

                IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on x64, 6 on x86.
                    // The UTF-16 DevicePath array begins 4 bytes after the structure start.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData,
                            detail, requiredSize, out _, IntPtr.Zero))
                        continue;
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    HidDisplay? display = TryOpen(path);
                    if (display != null) displays.Add(display);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(infoSet); }
        return displays;
    }

    private static HidDisplay? TryOpen(string path)
    {
        using var metadataHandle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (metadataHandle.IsInvalid) return null;
        var attributes = new Native.HidAttributes { Size = Marshal.SizeOf<Native.HidAttributes>() };
        if (!Native.HidD_GetAttributes(metadataHandle, ref attributes)) return null;
        if (attributes.VendorId != 0x05AC || !SupportedProductIds.Contains(attributes.ProductId))
            return null;

        string productName = $"Apple Cinema Display (PID 0x{attributes.ProductId:X4})";
        byte[] nameBuffer = new byte[512];
        if (Native.HidD_GetProductString(metadataHandle, nameBuffer, nameBuffer.Length))
        {
            string name = System.Text.Encoding.Unicode.GetString(nameBuffer).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(name)) productName = name;
        }

        // Close enumeration handle before trying exclusive access, as the original
        // application also opens some HID devices exclusively.
        metadataHandle.Dispose();
        SafeFileHandle? liveHandle = null;
        foreach (var (access, share) in new (uint Access, uint Share)[]
        {
            (0xC0000000, 3), (0x80000000, 3), (0x80000000, 0)
        })
        {
            SafeFileHandle candidate = Native.CreateFile(path, access, share,
                IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!candidate.IsInvalid) { liveHandle = candidate; break; }
            candidate.Dispose();
        }
        if (liveHandle == null) return null;

        bool ownershipTransferred = false;
        try
        {
            if (!Native.HidD_GetPreparsedData(liveHandle, out IntPtr prepared))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HID記述子を取得できません。");
            try
            {
                IntPtr capsPtr = Marshal.AllocHGlobal(128);
                try
                {
                    // HIDP_CAPS: FeatureReportByteLength at offset 8,
                    // NumberFeatureValueCaps at offset 60.
                    if (Native.HidP_GetCaps(prepared, capsPtr) < 0) return null;
                    int length = (ushort)Marshal.ReadInt16(capsPtr, 8);
                    int count = (ushort)Marshal.ReadInt16(capsPtr, 60);
                    if (length < 2 || length > 4096 || count <= 0 || count > 512)
                        return null;

                    // HIDP_VALUE_CAPS is 72 bytes, under the Win32 HID ABI.
                    const int stride = 72;
                    IntPtr values = Marshal.AllocHGlobal(count * stride);
                    try
                    {
                        ushort capacity = (ushort)count;
                        if (Native.HidP_GetValueCaps(2, values, ref capacity, prepared) < 0)
                            return null; // HidP_Feature == 2

                        (int Bits, int Min, int Max)? brightness = null;
                        bool auto = false;
                        for (int i = 0; i < capacity; i++)
                        {
                            IntPtr p = IntPtr.Add(values, i * stride);
                            ushort page = (ushort)Marshal.ReadInt16(p, 0);
                            byte reportId = Marshal.ReadByte(p, 2);
                            ushort usageMin = (ushort)Marshal.ReadInt16(p, 56);
                            ushort usageMax = Marshal.ReadByte(p, 12) != 0
                                ? (ushort)Marshal.ReadInt16(p, 58) : usageMin;
                            int bits = (ushort)Marshal.ReadInt16(p, 18);
                            int min = Marshal.ReadInt32(p, 40);
                            int max = Marshal.ReadInt32(p, 44);
                            if (page != 0x82) continue;
                            if (reportId == 0x10 && usageMin <= 0x10 && usageMax >= 0x10
                                && (bits is 8 or 16) && max > min)
                                brightness = (bits, min, max);
                            if (reportId == 0x66 && usageMin <= 0x66 && usageMax >= 0x66
                                && min == 1 && max == 2)
                                auto = true;
                        }

                        if (brightness == null) return null;
                        var value = brightness.Value;
                        var result = new HidDisplay(path, liveHandle, productName,
                            attributes.ProductId, length, value.Bits, value.Min, value.Max, auto);
                        ownershipTransferred = true;
                        return result;
                    }
                    finally { Marshal.FreeHGlobal(values); }
                }
                finally { Marshal.FreeHGlobal(capsPtr); }
            }
            finally { Native.HidD_FreePreparsedData(prepared); }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (!ownershipTransferred) liveHandle.Dispose();
        }
    }

    private byte[] MakeReport(byte id)
    {
        byte[] report = new byte[reportLength];
        report[0] = id;
        return report;
    }

    public int ReadBrightnessPercent()
    {
        byte[] report = MakeReport(0x10);
        if (!Native.HidD_GetFeature(handle, report, report.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "輝度の読み取りに失敗しました。");
        int raw = brightnessBits == 8 ? report[1] : BitConverter.ToUInt16(report, 1);
        return Math.Clamp((int)Math.Round((double)(raw - minimumBrightness) * 100 /
                           (maximumBrightness - minimumBrightness)), 0, 100);
    }

    public void SetBrightnessPercent(int percent)
    {
        percent = Math.Clamp(percent, 1, 100); // Avoid an unintentionally black display.
        int raw = minimumBrightness + (int)Math.Round((maximumBrightness - minimumBrightness) * percent / 100.0);
        byte[] report = MakeReport(0x10);
        if (brightnessBits == 8)
            report[1] = checked((byte)raw);
        else
        {
            byte[] bytes = BitConverter.GetBytes(checked((ushort)raw));
            report[1] = bytes[0];
            report[2] = bytes[1];
        }
        if (!Native.HidD_SetFeature(handle, report, report.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "輝度の変更に失敗しました。");
    }

    public bool ReadAutoBrightness()
    {
        if (!SupportsAutoBrightness) return false;
        byte[] report = MakeReport(0x66);
        if (!Native.HidD_GetFeature(handle, report, report.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return report[1] == 2;
    }

    public void SetAutoBrightness(bool enabled)
    {
        if (!SupportsAutoBrightness) return;
        byte[] report = MakeReport(0x66);
        report[1] = enabled ? (byte)2 : (byte)1;
        if (!Native.HidD_SetFeature(handle, report, report.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "自動輝度の切替に失敗しました。");
    }

    public void Dispose() => handle.Dispose();
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct HidAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll")]
    internal static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet,
        IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData, IntPtr detailData, uint detailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint desiredAccess,
        uint shareMode, IntPtr security, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_GetProductString(SafeFileHandle handle,
        [Out] byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetCaps(IntPtr preparsedData, IntPtr capabilities);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetValueCaps(int reportType,
        IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_GetFeature(SafeFileHandle handle,
        [In, Out] byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_SetFeature(SafeFileHandle handle,
        [In] byte[] reportBuffer, int reportBufferLength);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
