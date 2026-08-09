using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class CapxSetProfile
{
    private static string lastStage = "not-started";
    private static readonly Guid factoryIid = new Guid("705ECD13-3043-5208-982C-E19C05C64B60");
    private static readonly Guid proxyIid = new Guid("EB02895C-171F-5085-91C6-B7FE6C691948");

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out IntPtr hstring);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint initType);
    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateInstanceDelegate(IntPtr self, IntPtr instancePath, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBlobDelegate(IntPtr self, out uint size, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBlobDelegate(IntPtr self, uint size, IntPtr value);

    private static IntPtr VtableSlot(IntPtr instance, int slot)
    {
        return Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
    }

    private static T Delegate<T>(IntPtr instance, int slot) where T : class
    {
        return Marshal.GetDelegateForFunctionPointer<T>(VtableSlot(instance, slot));
    }

    private static string Hr(int value)
    {
        return "0x" + value.ToString("X8");
    }

    private static string Json(string value)
    {
        if (value == null) return "null";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static int HexDigit(char value)
    {
        if (value >= '0' && value <= '9') return value - '0';
        if (value >= 'a' && value <= 'f') return value - 'a' + 10;
        if (value >= 'A' && value <= 'F') return value - 'A' + 10;
        return -1;
    }

    private static byte[] ParseHex(string value)
    {
        if (String.IsNullOrEmpty(value) || (value.Length % 2) != 0) {
            throw new ArgumentException("Profile hex must have an even length.");
        }
        byte[] result = new byte[value.Length / 2];
        for (int i = 0; i < result.Length; i++) {
            int high = HexDigit(value[i * 2]);
            int low = HexDigit(value[i * 2 + 1]);
            if (high < 0 || low < 0) throw new ArgumentException("Profile hex contains an invalid character.");
            result[i] = (byte)((high << 4) | low);
        }
        return result;
    }

    private static string Hex(IntPtr buffer, uint size)
    {
        if (buffer == IntPtr.Zero || size == 0) return String.Empty;
        byte[] bytes = new byte[size];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
        StringBuilder result = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) result.Append(value.ToString("x2"));
        return result.ToString();
    }

    private static string ReadProfile(IntPtr proxy, out int hr)
    {
        uint size;
        IntPtr buffer;
        hr = Delegate<GetBlobDelegate>(proxy, 11)(proxy, out size, out buffer);
        try {
            return hr < 0 ? null : Hex(buffer, size);
        }
        finally {
            if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static string Execute(string endpointId, byte[] profile, bool apply)
    {
        IntPtr className = IntPtr.Zero;
        IntPtr endpointString = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        IntPtr created = IntPtr.Zero;
        IntPtr proxy = IntPtr.Zero;
        IntPtr profileBuffer = IntPtr.Zero;
        int initHr = RoInitialize(1);
        try {
            lastStage = "create-class-string";
            int hr = WindowsCreateString("CapxComponent.PropertyStoreProxy", 32, out className);
            if (hr < 0) throw new COMException("WindowsCreateString(class)", hr);

            lastStage = "get-activation-factory";
            Guid iid = factoryIid;
            hr = RoGetActivationFactory(className, ref iid, out factory);
            if (hr < 0) throw new COMException("RoGetActivationFactory", hr);

            lastStage = "create-endpoint-string";
            hr = WindowsCreateString(endpointId, endpointId.Length, out endpointString);
            if (hr < 0) throw new COMException("WindowsCreateString(endpoint)", hr);

            lastStage = "create-instance";
            int createHr = Delegate<CreateInstanceDelegate>(factory, 6)(factory, endpointString, out created);
            if (createHr < 0) throw new COMException("CreateInstance", createHr);

            lastStage = "query-interface";
            Guid proxyGuid = proxyIid;
            int queryHr = Delegate<QueryInterfaceDelegate>(created, 0)(created, ref proxyGuid, out proxy);
            if (queryHr < 0) throw new COMException("QueryInterface", queryHr);

            lastStage = "read-before";
            int beforeHr;
            string before = ReadProfile(proxy, out beforeHr);

            int setHr = unchecked((int)0x80004001);
            if (apply) {
                lastStage = "set-atmos-profile";
                profileBuffer = Marshal.AllocCoTaskMem(profile.Length);
                Marshal.Copy(profile, 0, profileBuffer, profile.Length);
                setHr = Delegate<SetBlobDelegate>(proxy, 15)(proxy, (uint)profile.Length, profileBuffer);
            }

            lastStage = "read-after";
            int afterHr;
            string after = ReadProfile(proxy, out afterHr);
            string requested = BitConverter.ToString(profile).Replace("-", "").ToLowerInvariant();

            return "{" +
                "\"requestedProfile\":" + Json(requested) + "," +
                "\"apply\":" + (apply ? "true" : "false") + "," +
                "\"before\":{\"hr\":" + Json(Hr(beforeHr)) + ",\"hex\":" + Json(before) + "}," +
                "\"set\":{\"hr\":" + Json(Hr(setHr)) + "}," +
                "\"after\":{\"hr\":" + Json(Hr(afterHr)) + ",\"hex\":" + Json(after) + "}" +
                "}";
        }
        finally {
            if (profileBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(profileBuffer);
            if (proxy != IntPtr.Zero) Marshal.Release(proxy);
            if (created != IntPtr.Zero) Marshal.Release(created);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (endpointString != IntPtr.Zero) WindowsDeleteString(endpointString);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
            if (initHr == 0) RoUninitialize();
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length < 3 || args.Length > 4) {
            Console.Error.WriteLine("Usage: CapxSetProfile.exe <endpoint-id-or-@file> <profile-hex> <output-json> [--apply]");
            return 2;
        }
        try {
            string endpoint = args[0];
            if (endpoint.StartsWith("@", StringComparison.Ordinal)) {
                endpoint = File.ReadAllText(endpoint.Substring(1)).Trim();
            }
            byte[] profile = ParseHex(args[1]);
            bool apply = args.Length == 4 && String.Equals(args[3], "--apply", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(args[2], Execute(endpoint, profile, apply), Encoding.UTF8);
            return 0;
        }
        catch (Exception ex) {
            string error = "{\"errorType\":" + Json(ex.GetType().FullName) + ",\"message\":" + Json(ex.Message) + ",\"hresult\":" + Json(Hr(ex.HResult)) + ",\"stage\":" + Json(lastStage) + ",\"details\":" + Json(ex.ToString()) + "}";
            File.WriteAllText(args[2], error, Encoding.UTF8);
            return 1;
        }
    }
}
