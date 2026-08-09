using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class DtsSetProfile
{
    private const int RoInitMultithreaded = 1;
    private const int InterfaceFirstMethodSlot = 6;
    private static readonly Guid CapxSettingsInterfaceId = new("ECACB328-8789-31DC-9B2F-149248908B52");
    private static string lastStage = "not-started";

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint initType);

    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [DllImport("combase.dll")]
    private static extern int RoActivateInstance(nint activatableClassId, out nint instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(nint self, ref Guid iid, out nint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsCapxSupportedDelegate(nint self, nint deviceId, out byte value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TryIncrementTransactionCounterDelegate(nint self, nint deviceId, out byte value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TryWriteSadBlobDelegate(nint self, nint deviceId, uint blobSize, nint blob, out byte value);

    private static nint VtableSlot(nint instance, int slot) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size);

    private static T Delegate<T>(nint instance, int slot) where T : class =>
        Marshal.GetDelegateForFunctionPointer<T>(VtableSlot(instance, slot));

    private static string Hr(int value) => "0x" + value.ToString("X8");

    private static string Json(string? value)
    {
        if (value == null) return "null";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static string EndpointId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (value.Contains("MMDEVAPI#", StringComparison.OrdinalIgnoreCase))
        {
            int marker = value.IndexOf("MMDEVAPI#", StringComparison.OrdinalIgnoreCase);
            string coreId = value[(marker + "MMDEVAPI#".Length)..];
            int interfaceMarker = coreId.IndexOf("#{", StringComparison.Ordinal);
            return interfaceMarker > 0 ? coreId[..interfaceMarker] : coreId;
        }

        if (value.StartsWith("{", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal) &&
            !value.StartsWith("{0.0.00000000}.", StringComparison.OrdinalIgnoreCase))
        {
            return "{0.0.00000000}." + value;
        }

        return value;
    }

    private static string[] DeviceIdCandidates(string value)
    {
        string normalized = EndpointId(value);
        return new[] { normalized, value }
            .WhereNotEmpty()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] WhereNotEmpty(this IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

    private static string Execute(string endpoint, string? profilePath, bool apply, bool force)
    {
        nint className = 0;
        nint deviceId = 0;
        nint instance = 0;
        nint settings = 0;
        nint blob = 0;
        int initHr = RoInitialize(RoInitMultithreaded);
        try
        {
            lastStage = "create-class-string";
            int hr = WindowsCreateString("CapxSettingsInterface.CapxSettings", "CapxSettingsInterface.CapxSettings".Length, out className);
            if (hr < 0) throw new COMException("WindowsCreateString(class)", hr);

            lastStage = "activate-instance";
            hr = RoActivateInstance(className, out instance);
            if (hr < 0) throw new COMException("RoActivateInstance", hr);

            lastStage = "query-interface";
            Guid interfaceId = CapxSettingsInterfaceId;
            hr = Delegate<QueryInterfaceDelegate>(instance, 0)(instance, ref interfaceId, out settings);
            if (hr < 0) throw new COMException("QueryInterface(CapxSettingsInterface)", hr);

            string[] candidates = DeviceIdCandidates(endpoint);
            string? selectedDeviceId = null;
            byte supported = 0;
            foreach (string candidate in candidates)
            {
                lastStage = "create-device-string";
                hr = WindowsCreateString(candidate, candidate.Length, out deviceId);
                if (hr < 0) throw new COMException("WindowsCreateString(device)", hr);

                lastStage = "is-capx-supported";
                hr = Delegate<IsCapxSupportedDelegate>(settings, InterfaceFirstMethodSlot)(settings, deviceId, out supported);
                WindowsDeleteString(deviceId);
                deviceId = 0;
                if (hr < 0) throw new COMException("IsCAPxSupported", hr);
                if (supported != 0)
                {
                    selectedDeviceId = candidate;
                    break;
                }
            }

            bool forced = false;
            if (selectedDeviceId == null || supported == 0)
            {
                if (!apply || !force)
                {
                    return "{\"supported\":false,\"forced\":false,\"deviceId\":null,\"applied\":false}";
                }

                // The caller explicitly requested a live write. Keep the
                // first normalized endpoint candidate and let the Unbound
                // provider accept or reject the actual transaction.
                selectedDeviceId = candidates.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(selectedDeviceId))
                {
                    return "{\"supported\":false,\"forced\":true,\"deviceId\":null,\"applied\":false}";
                }

                forced = true;
            }

            if (!apply)
            {
                return "{\"supported\":true,\"forced\":false,\"deviceId\":" + Json(selectedDeviceId) + ",\"applied\":false}";
            }

            if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
            {
                throw new FileNotFoundException("The DTS SAD profile blob is missing.", profilePath);
            }

            byte[] sadBlob = File.ReadAllBytes(profilePath);
            if (sadBlob.Length == 0) throw new InvalidDataException("The DTS SAD profile blob is empty.");

            lastStage = "create-device-string-for-write";
            hr = WindowsCreateString(selectedDeviceId, selectedDeviceId.Length, out deviceId);
            if (hr < 0) throw new COMException("WindowsCreateString(device)", hr);

            lastStage = "increment-transaction-counter";
            byte transactionResult;
            hr = Delegate<TryIncrementTransactionCounterDelegate>(settings, InterfaceFirstMethodSlot + 1)(settings, deviceId, out transactionResult);
            if (hr < 0 && !force) throw new COMException("TryIncrementTransationCounter", hr);
            if (hr < 0) transactionResult = 0;

            lastStage = "write-sad-blob";
            blob = Marshal.AllocCoTaskMem(sadBlob.Length);
            Marshal.Copy(sadBlob, 0, blob, sadBlob.Length);
            byte writeResult;
            hr = Delegate<TryWriteSadBlobDelegate>(settings, InterfaceFirstMethodSlot + 3)(settings, deviceId, (uint)sadBlob.Length, blob, out writeResult);
            if (hr < 0) throw new COMException("TryWriteSadBlobToCapxPropertyStore", hr);

            return "{\"supported\":" + (supported != 0 ? "true" : "false") +
                   ",\"forced\":" + (forced ? "true" : "false") +
                   ",\"deviceId\":" + Json(selectedDeviceId) +
                   ",\"blobBytes\":" + sadBlob.Length +
                   ",\"transaction\":" + (transactionResult != 0 ? "true" : "false") +
                   ",\"sadWrite\":" + (writeResult != 0 ? "true" : "false") +
                   ",\"applied\":" + (writeResult != 0 ? "true" : "false") + "}";
        }
        finally
        {
            if (blob != 0) Marshal.FreeCoTaskMem(blob);
            if (deviceId != 0) WindowsDeleteString(deviceId);
            if (settings != 0) Marshal.Release(settings);
            if (instance != 0) Marshal.Release(instance);
            if (className != 0) WindowsDeleteString(className);
            if (initHr == 0) RoUninitialize();
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length < 3 || args.Length > 5)
        {
            Console.Error.WriteLine("Usage: DtsSetProfile.exe <endpoint-id-or-@file> <sad-profile-file-or-> <output-json> [--apply] [--force]");
            return 2;
        }

        try
        {
            string endpoint = args[0];
            if (endpoint.StartsWith("@", StringComparison.Ordinal)) endpoint = File.ReadAllText(endpoint[1..]).Trim();
            string? profilePath = args[1] == "-" ? null : args[1];
            bool apply = args.Any(arg => string.Equals(arg, "--apply", StringComparison.OrdinalIgnoreCase));
            bool force = args.Any(arg => string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase));
            string output = Execute(endpoint, profilePath, apply, force);
            File.WriteAllText(args[2], output, Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            string error = "{\"errorType\":" + Json(ex.GetType().FullName) + ",\"message\":" + Json(ex.Message) +
                           ",\"hresult\":" + Json(Hr(ex.HResult)) + ",\"stage\":" + Json(lastStage) +
                           ",\"details\":" + Json(ex.ToString()) + "}";
            try { File.WriteAllText(args[2], error, Encoding.UTF8); } catch { }
            return 1;
        }
    }
}
