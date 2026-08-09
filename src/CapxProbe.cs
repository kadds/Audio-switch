using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Read-only probe for CapxComponent.PropertyStoreProxy.
// This is the Atmos codec/property-store path used by Dolby Access.
internal static class CapxProbe
{
    private static string LastStage = "not-started";
    private static readonly Guid ActivationFactoryIid = new Guid("705ECD13-3043-5208-982C-E19C05C64B60");
    private static readonly Guid PropertyStoreProxyIid = new Guid("EB02895C-171F-5085-91C6-B7FE6C691948");

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out int length);

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
    private delegate int GetStringDelegate(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBoolDelegate(IntPtr self, out byte value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBlobDelegate(IntPtr self, out uint size, out IntPtr value);

    private static IntPtr GetVtableSlot(IntPtr instance, int slot)
    {
        return Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
    }

    private static T GetDelegate<T>(IntPtr instance, int slot) where T : class
    {
        return Marshal.GetDelegateForFunctionPointer<T>(GetVtableSlot(instance, slot));
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

    private static string Hex(IntPtr buffer, uint size)
    {
        if (buffer == IntPtr.Zero || size == 0) return string.Empty;
        byte[] bytes = new byte[size];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
        StringBuilder result = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) result.Append(value.ToString("x2"));
        return result.ToString();
    }

    private static string ReadString(IntPtr value)
    {
        if (value == IntPtr.Zero) return null;
        try
        {
            int length;
            IntPtr buffer = WindowsGetStringRawBuffer(value, out length);
            return buffer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(buffer, length);
        }
        finally
        {
            WindowsDeleteString(value);
        }
    }

    private static string Probe(string endpointId)
    {
        IntPtr className = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        IntPtr createdProxy = IntPtr.Zero;
        IntPtr proxy = IntPtr.Zero;
        IntPtr endpoint = IntPtr.Zero;
        int initRo = RoInitialize(1);
        try
        {
            LastStage = "create-class-string";
            int hr = WindowsCreateString("CapxComponent.PropertyStoreProxy", 32, out className);
            if (hr < 0) throw new COMException("WindowsCreateString(class)", hr);

            LastStage = "get-activation-factory";
            Guid factoryIid = ActivationFactoryIid;
            hr = RoGetActivationFactory(className, ref factoryIid, out factory);
            if (hr < 0) throw new COMException("RoGetActivationFactory", hr);

            LastStage = "create-endpoint-string";
            hr = WindowsCreateString(endpointId, endpointId.Length, out endpoint);
            if (hr < 0) throw new COMException("WindowsCreateString(endpoint)", hr);

            LastStage = "factory-create-instance";
            IntPtr created;
            int createHr = GetDelegate<CreateInstanceDelegate>(factory, 6)(factory, endpoint, out created);
            if (createHr < 0) throw new COMException("IPropertyStoreProxyFactory.CreateInstance", createHr);
            createdProxy = created;

            LastStage = "query-interface";
            Guid proxyIid = PropertyStoreProxyIid;
            int qiHr = GetDelegate<QueryInterfaceDelegate>(createdProxy, 0)(createdProxy, ref proxyIid, out proxy);
            if (qiHr < 0) throw new COMException("QueryInterface(IPropertyStoreProxy)", qiHr);

            LastStage = "get-endpoint-id";
            IntPtr returnedEndpoint;
            int endpointHr = GetDelegate<GetStringDelegate>(proxy, 6)(proxy, out returnedEndpoint);

            LastStage = "is-supported";
            byte supported;
            int supportedHr = GetDelegate<GetBoolDelegate>(proxy, 7)(proxy, out supported);

            // Some builds expose a broken/missing update-counter implementation;
            // skip it so the profile blob can still be read.
            int counterHr = unchecked((int)0x80004001);
            string counter = null;

            LastStage = "get-atmos-profile";
            uint profileSize;
            IntPtr profileBuffer;
            int profileHr = GetDelegate<GetBlobDelegate>(proxy, 11)(proxy, out profileSize, out profileBuffer);
            string profileHex = profileHr < 0 ? null : Hex(profileBuffer, profileSize);
            if (profileBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(profileBuffer);

            LastStage = "format-result";
            string actualEndpoint = endpointHr < 0 ? null : ReadString(returnedEndpoint);
            return "{" +
                "\"inputEndpoint\":" + Json(endpointId) + "," +
                "\"createInstance\":{" + "\"hr\":" + Json(Hr(createHr)) + "}," +
                "\"queryInterface\":{" + "\"hr\":" + Json(Hr(qiHr)) + "}," +
                "\"endpoint\":{" + "\"hr\":" + Json(Hr(endpointHr)) + ",\"value\":" + Json(actualEndpoint) + "}," +
                "\"isSupported\":{" + "\"hr\":" + Json(Hr(supportedHr)) + ",\"value\":" + (supported == 0 ? "false" : "true") + "}," +
                "\"updateCounter\":{" + "\"hr\":" + Json(Hr(counterHr)) + ",\"value\":" + Json(counter) + "}," +
                "\"atmosProfile\":{" + "\"hr\":" + Json(Hr(profileHr)) + ",\"size\":" + profileSize + ",\"hex\":" + Json(profileHex) + "}" +
                "}";
        }
        finally
        {
            if (endpoint != IntPtr.Zero) WindowsDeleteString(endpoint);
            if (proxy != IntPtr.Zero) Marshal.Release(proxy);
            if (createdProxy != IntPtr.Zero) Marshal.Release(createdProxy);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
            if (initRo == 0) RoUninitialize();
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: CapxProbe.exe <endpoint-id> <output-json>");
            return 2;
        }

        try
        {
            string endpointId = args[0];
            if (endpointId.StartsWith("@", StringComparison.Ordinal))
            {
                endpointId = File.ReadAllText(endpointId.Substring(1)).Trim();
            }

            File.WriteAllText(args[1], Probe(endpointId), Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            string error = "{\"errorType\":" + Json(ex.GetType().FullName) + ",\"message\":" + Json(ex.Message) + ",\"hresult\":" + Json(Hr(ex.HResult)) + ",\"stage\":" + Json(LastStage) + ",\"details\":" + Json(ex.ToString()) + "}";
            File.WriteAllText(args[1], error, Encoding.UTF8);
            return 1;
        }
    }
}
