using System.Runtime.InteropServices;

namespace AudioSwitch_WinUI;

public enum AudioCurveCaptureState
{
    Stopped,
    Starting,
    Listening,
    NoSignal,
    Error
}

public sealed record AudioCurveSnapshot(
    float[] Samples,
    float[] LowFrequencyLevels,
    float[] MidFrequencyLevels,
    float[] HighFrequencyLevels,
    float RmsPercent,
    float PeakPercent,
    int SampleRate,
    string EndpointPath,
    AudioCurveCaptureState State,
    string ErrorMessage);

/// <summary>
/// Read-only WASAPI loopback capture for the current render endpoint.
/// It observes the output stream without changing the device, volume, or spatial format.
/// </summary>
public sealed class AudioCurveCapture : IDisposable
{
    private const string MmDeviceEnumeratorClsid = "BCDE0395-E52F-467C-8E3D-C4579291692E";
    private const string MmDeviceEnumeratorIid = "A95664D2-9614-4F35-A746-DE8DB63617E6";
    private const string MmDeviceIid = "D666063F-1587-4E43-81F1-B948E807363F";
    private const string AudioClientIid = "1CB9AD4C-DBFA-4c32-B178-C2F568A703B2";
    private const string AudioCaptureClientIid = "C8ADBD64-E71E-48a0-A4DE-185C395CD317";
    private const uint ClsCtxInprocServer = 0x1;
    private const uint StreamFlagLoopback = 0x00020000;
    private const uint BufferFlagSilent = 0x2;
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private const int SampleCount = 1024;
    private const int FrequencyHistoryCount = 128;

    private readonly object sync = new();
    private readonly float[] waveform = new float[SampleCount];
    private readonly float[] lowFrequencyHistory = new float[FrequencyHistoryCount];
    private readonly float[] midFrequencyHistory = new float[FrequencyHistoryCount];
    private readonly float[] highFrequencyHistory = new float[FrequencyHistoryCount];
    private readonly float[] fftReal = new float[SampleCount];
    private readonly float[] fftImaginary = new float[SampleCount];
    private CancellationTokenSource? captureCts;
    private Task? captureTask;
    private int waveformWriteIndex;
    private double sumSquares;
    private float peak;
    private int metricCount;
    private int frequencyWriteIndex;
    private AudioCurveSnapshot snapshot = CreateSnapshot(
        Array.Empty<float>(),
        Array.Empty<float>(),
        Array.Empty<float>(),
        Array.Empty<float>(),
        0,
        0,
        0,
        string.Empty,
        AudioCurveCaptureState.Stopped,
        string.Empty);
    private string requestedEndpointPath = string.Empty;

    public AudioCurveSnapshot Snapshot()
    {
        lock (sync)
        {
            return snapshot with
            {
                Samples = snapshot.Samples.ToArray(),
                LowFrequencyLevels = snapshot.LowFrequencyLevels.ToArray(),
                MidFrequencyLevels = snapshot.MidFrequencyLevels.ToArray(),
                HighFrequencyLevels = snapshot.HighFrequencyLevels.ToArray()
            };
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (sync) return captureCts != null;
        }
    }

    public void Start(string? endpointPath)
    {
        string normalizedPath = endpointPath ?? string.Empty;
        lock (sync)
        {
            if (captureCts != null) return;

            requestedEndpointPath = normalizedPath;
            ClearWaveformLocked();
            captureCts = new CancellationTokenSource();
            snapshot = CreateSnapshot(
                new float[SampleCount],
                new float[FrequencyHistoryCount],
                new float[FrequencyHistoryCount],
                new float[FrequencyHistoryCount],
                0,
                0,
                0,
                normalizedPath,
                AudioCurveCaptureState.Starting,
                string.Empty);
            CancellationTokenSource session = captureCts!;
            captureTask = Task.Run(() => CaptureLoop(session, normalizedPath));
        }
    }

    public void SetEndpoint(string? endpointPath)
    {
        string normalizedPath = endpointPath ?? string.Empty;
        bool restart;
        lock (sync)
        {
            restart = captureCts != null && !string.Equals(requestedEndpointPath, normalizedPath, StringComparison.OrdinalIgnoreCase);
        }

        if (!restart) return;
        Stop();
        Start(normalizedPath);
    }

    public void Stop()
    {
        lock (sync)
        {
            captureCts?.Cancel();
            captureCts = null;
            captureTask = null;
            requestedEndpointPath = string.Empty;
            snapshot = snapshot with
            {
                State = AudioCurveCaptureState.Stopped,
                ErrorMessage = string.Empty
            };
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void CaptureLoop(CancellationTokenSource session, string endpointPath)
    {
        CancellationToken cancellationToken = session.Token;
        bool comInitialized = false;
        try
        {
            int coInitializeResult = CoInitializeEx(0, 0x0);
            comInitialized = coInitializeResult >= 0;
            if (!comInitialized)
            {
                SetError(endpointPath, $"COM initialization failed: 0x{coInitializeResult:X8}");
                return;
            }

            if (!TryOpenCaptureClient(endpointPath, out IAudioClient? audioClient, out IAudioCaptureClient? captureClient, out WaveFormatInfo format, out string error))
            {
                SetError(endpointPath, error);
                return;
            }

            try
            {
                int startResult = audioClient!.Start();
                if (startResult < 0)
                {
                    SetError(endpointPath, $"WASAPI loopback start failed: 0x{startResult:X8}");
                    return;
                }

                SetState(endpointPath, format.SampleRate, AudioCurveCaptureState.Listening, string.Empty);
                byte[] packetBuffer = Array.Empty<byte>();
                while (!cancellationToken.IsCancellationRequested)
                {
                    bool receivedPacket = false;
                    int packetResult = captureClient!.GetNextPacketSize(out uint packetFrames);
                    if (packetResult < 0)
                    {
                        SetError(endpointPath, $"WASAPI packet query failed: 0x{packetResult:X8}");
                        break;
                    }

                    while (packetFrames > 0 && !cancellationToken.IsCancellationRequested)
                    {
                        receivedPacket = true;
                        int bufferResult = captureClient.GetBuffer(
                            out nint data,
                            out uint frames,
                            out uint flags,
                            out _,
                            out _);
                        if (bufferResult < 0)
                        {
                            SetError(endpointPath, $"WASAPI buffer read failed: 0x{bufferResult:X8}");
                            break;
                        }

                        try
                        {
                            int byteCount = checked((int)(frames * (uint)format.BlockAlign));
                            if ((flags & BufferFlagSilent) != 0 || data == 0)
                            {
                                AppendSilence((int)frames, endpointPath, format.SampleRate);
                            }
                            else
                            {
                                if (packetBuffer.Length < byteCount) packetBuffer = new byte[byteCount];
                                Marshal.Copy(data, packetBuffer, 0, byteCount);
                                AppendSamples(packetBuffer, (int)frames, format, endpointPath);
                            }
                        }
                        finally
                        {
                            captureClient.ReleaseBuffer(frames);
                        }

                        packetResult = captureClient.GetNextPacketSize(out packetFrames);
                        if (packetResult < 0)
                        {
                            SetError(endpointPath, $"WASAPI packet query failed: 0x{packetResult:X8}");
                            break;
                        }
                    }

                    if (!receivedPacket) Thread.Sleep(10);
                }

                audioClient.Stop();
            }
            finally
            {
                ReleaseComObject(captureClient);
                ReleaseComObject(audioClient);
            }
        }
        catch (Exception ex)
        {
            SetError(endpointPath, ex.Message);
        }
        finally
        {
            if (comInitialized) CoUninitialize();
            lock (sync)
            {
                if (ReferenceEquals(captureCts, session))
                {
                    captureCts = null;
                    captureTask = null;
                    if (snapshot.State != AudioCurveCaptureState.Error)
                    {
                        snapshot = snapshot with { State = AudioCurveCaptureState.Stopped };
                    }
                }
            }
        }
    }

    private bool TryOpenCaptureClient(
        string endpointPath,
        out IAudioClient? audioClient,
        out IAudioCaptureClient? captureClient,
        out WaveFormatInfo format,
        out string error)
    {
        audioClient = null;
        captureClient = null;
        format = default;
        error = string.Empty;
        object? enumeratorObject = null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        nint audioClientPointer = 0;
        nint formatPointer = 0;
        nint captureClientPointer = 0;

        try
        {
            enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(MmDeviceEnumeratorClsid), throwOnError: true)!);
            enumerator = (IMMDeviceEnumerator)enumeratorObject!;
            string coreAudioEndpointPath = AudioEndpointChoice.NormalizeCoreAudioEndpointId(endpointPath);
            int deviceResult = string.IsNullOrWhiteSpace(endpointPath)
                ? enumerator.GetDefaultAudioEndpoint(0, 0, out device)
                : enumerator.GetDevice(coreAudioEndpointPath, out device);
            if (deviceResult < 0 && !string.Equals(coreAudioEndpointPath, endpointPath, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseComObject(device);
                device = null;
                deviceResult = enumerator.GetDevice(endpointPath, out device);
            }
            if (deviceResult < 0 || device == null)
            {
                error = $"Output endpoint could not be opened: 0x{deviceResult:X8}";
                return false;
            }

            Guid audioClientGuid = new(AudioClientIid);
            int activateResult = device.Activate(ref audioClientGuid, ClsCtxInprocServer, 0, out audioClientPointer);
            if (activateResult < 0 || audioClientPointer == 0)
            {
                error = $"Audio client could not be activated: 0x{activateResult:X8}";
                return false;
            }

            audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(audioClientPointer);
            int formatResult = audioClient.GetMixFormat(out formatPointer);
            if (formatResult < 0 || formatPointer == 0)
            {
                error = $"Mix format could not be read: 0x{formatResult:X8}";
                return false;
            }

            format = WaveFormatInfo.Read(formatPointer);
            Guid sessionGuid = Guid.Empty;
            int initializeResult = audioClient.Initialize(
                0,
                StreamFlagLoopback,
                1_000_000,
                0,
                formatPointer,
                ref sessionGuid);
            if (initializeResult < 0)
            {
                error = $"WASAPI loopback initialization failed: 0x{initializeResult:X8}";
                return false;
            }

            Guid captureClientGuid = new(AudioCaptureClientIid);
            int serviceResult = audioClient.GetService(ref captureClientGuid, out captureClientPointer);
            if (serviceResult < 0 || captureClientPointer == 0)
            {
                error = $"Loopback capture service could not be opened: 0x{serviceResult:X8}";
                return false;
            }

            captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(captureClientPointer);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Loopback capture could not be opened: {ex.Message}";
            return false;
        }
        finally
        {
            if (formatPointer != 0) CoTaskMemFree(formatPointer);
            if (captureClientPointer != 0) Marshal.Release(captureClientPointer);
            if (audioClientPointer != 0) Marshal.Release(audioClientPointer);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
            if (enumerator == null) ReleaseComObject(enumeratorObject);
        }
    }

    private void AppendSamples(byte[] buffer, int frames, WaveFormatInfo format, string endpointPath)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0 || format.BlockAlign < bytesPerSample * format.Channels)
        {
            SetError(endpointPath, "Unsupported output sample format.");
            return;
        }

        lock (sync)
        {
            if (captureCts == null || !string.Equals(requestedEndpointPath, endpointPath, StringComparison.OrdinalIgnoreCase)) return;
            for (int frame = 0; frame < frames; frame++)
            {
                int offset = frame * format.BlockAlign;
                float sample = ReadSample(buffer, offset, bytesPerSample, format.IsFloat);
                AppendSampleLocked(sample);
            }

            PublishMetricsLocked(endpointPath, format.SampleRate, Math.Abs(waveform[(waveformWriteIndex + SampleCount - 1) % SampleCount]) > 0.001f);
        }
    }

    private void AppendSilence(int frames, string endpointPath, int sampleRate)
    {
        lock (sync)
        {
            if (captureCts == null || !string.Equals(requestedEndpointPath, endpointPath, StringComparison.OrdinalIgnoreCase)) return;
            for (int index = 0; index < frames; index++) AppendSampleLocked(0);
            PublishMetricsLocked(endpointPath, sampleRate, false);
        }
    }

    private void AppendSampleLocked(float sample)
    {
        sample = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0;
        float previous = waveform[waveformWriteIndex];
        waveform[waveformWriteIndex] = sample;
        waveformWriteIndex = (waveformWriteIndex + 1) % SampleCount;
        sumSquares += sample * sample - previous * previous;
        peak = Math.Max(peak, Math.Abs(sample));
        metricCount = Math.Min(metricCount + 1, SampleCount);
    }

    private void PublishMetricsLocked(string endpointPath, int sampleRate, bool hasSignal)
    {
        float[] orderedSamples = new float[SampleCount];
        for (int index = 0; index < SampleCount; index++)
        {
            orderedSamples[index] = waveform[(waveformWriteIndex + index) % SampleCount];
        }

        (float[] lowLevels, float[] midLevels, float[] highLevels) = UpdateFrequencyHistoryLocked(orderedSamples, sampleRate);

        float divisor = Math.Max(1, metricCount);
        float rms = (float)Math.Sqrt(Math.Max(0, sumSquares) / divisor);
        snapshot = CreateSnapshot(
            orderedSamples,
            lowLevels,
            midLevels,
            highLevels,
            rms * 100f,
            peak * 100f,
            sampleRate,
            endpointPath,
            hasSignal ? AudioCurveCaptureState.Listening : AudioCurveCaptureState.NoSignal,
            string.Empty);
        peak = 0;
    }

    private void ClearWaveformLocked()
    {
        Array.Clear(waveform);
        Array.Clear(lowFrequencyHistory);
        Array.Clear(midFrequencyHistory);
        Array.Clear(highFrequencyHistory);
        Array.Clear(fftReal);
        Array.Clear(fftImaginary);
        waveformWriteIndex = 0;
        frequencyWriteIndex = 0;
        sumSquares = 0;
        peak = 0;
        metricCount = 0;
    }

    private void SetState(string endpointPath, int sampleRate, AudioCurveCaptureState state, string error)
    {
        lock (sync)
        {
            if (captureCts == null || !string.Equals(requestedEndpointPath, endpointPath, StringComparison.OrdinalIgnoreCase)) return;
            snapshot = snapshot with
            {
                SampleRate = sampleRate,
                EndpointPath = endpointPath,
                State = state,
                ErrorMessage = error
            };
        }
    }

    private void SetError(string endpointPath, string error)
    {
        lock (sync)
        {
            if (captureCts == null || !string.Equals(requestedEndpointPath, endpointPath, StringComparison.OrdinalIgnoreCase)) return;
            snapshot = snapshot with
            {
                EndpointPath = endpointPath,
                State = AudioCurveCaptureState.Error,
                ErrorMessage = error
            };
        }
    }

    private static float ReadSample(byte[] buffer, int offset, int bytesPerSample, bool isFloat)
    {
        if (isFloat && bytesPerSample >= 4) return BitConverter.ToSingle(buffer, offset);
        return bytesPerSample switch
        {
            1 => (buffer[offset] - 128) / 128f,
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => Read24BitSample(buffer, offset),
            4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => 0
        };
    }

    private static float Read24BitSample(byte[] buffer, int offset)
    {
        int value = buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16;
        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
        return value / 8388608f;
    }

    private (float[] Low, float[] Mid, float[] High) UpdateFrequencyHistoryLocked(float[] samples, int sampleRate)
    {
        Array.Clear(fftReal);
        Array.Clear(fftImaginary);
        for (int index = 0; index < SampleCount; index++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (SampleCount - 1));
            fftReal[index] = samples[index] * (float)window;
        }

        for (int index = 1, reversed = 0; index < SampleCount; index++)
        {
            int bit = SampleCount >> 1;
            for (; (reversed & bit) != 0; bit >>= 1) reversed ^= bit;
            reversed ^= bit;
            if (index >= reversed) continue;
            (fftReal[index], fftReal[reversed]) = (fftReal[reversed], fftReal[index]);
            (fftImaginary[index], fftImaginary[reversed]) = (fftImaginary[reversed], fftImaginary[index]);
        }

        for (int length = 2; length <= SampleCount; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            float phaseReal = (float)Math.Cos(angle);
            float phaseImaginary = (float)Math.Sin(angle);
            int halfLength = length >> 1;
            for (int start = 0; start < SampleCount; start += length)
            {
                float currentReal = 1;
                float currentImaginary = 0;
                for (int offset = 0; offset < halfLength; offset++)
                {
                    int evenIndex = start + offset;
                    int oddIndex = evenIndex + halfLength;
                    float oddReal = fftReal[oddIndex] * currentReal - fftImaginary[oddIndex] * currentImaginary;
                    float oddImaginary = fftReal[oddIndex] * currentImaginary + fftImaginary[oddIndex] * currentReal;
                    float evenReal = fftReal[evenIndex];
                    float evenImaginary = fftImaginary[evenIndex];
                    fftReal[evenIndex] = evenReal + oddReal;
                    fftImaginary[evenIndex] = evenImaginary + oddImaginary;
                    fftReal[oddIndex] = evenReal - oddReal;
                    fftImaginary[oddIndex] = evenImaginary - oddImaginary;
                    float nextReal = currentReal * phaseReal - currentImaginary * phaseImaginary;
                    currentImaginary = currentReal * phaseImaginary + currentImaginary * phaseReal;
                    currentReal = nextReal;
                }
            }
        }

        double lowPower = 0;
        double midPower = 0;
        double highPower = 0;
        int lowBins = 0;
        int midBins = 0;
        int highBins = 0;
        double binWidth = sampleRate / (double)SampleCount;
        for (int bin = 1; bin <= SampleCount / 2; bin++)
        {
            double frequency = bin * binWidth;
            double magnitudeSquared = (fftReal[bin] * fftReal[bin] + fftImaginary[bin] * fftImaginary[bin]) * 4 / (SampleCount * (double)SampleCount);
            if (frequency >= 20 && frequency < 250)
            {
                lowPower += magnitudeSquared;
                lowBins++;
            }
            else if (frequency >= 250 && frequency < 4000)
            {
                midPower += magnitudeSquared;
                midBins++;
            }
            else if (frequency >= 4000 && frequency <= 20000)
            {
                highPower += magnitudeSquared;
                highBins++;
            }
        }

        float lowLevel = NormalizeFrequencyLevel(lowPower, lowBins);
        float midLevel = NormalizeFrequencyLevel(midPower, midBins);
        float highLevel = NormalizeFrequencyLevel(highPower, highBins);
        lowFrequencyHistory[frequencyWriteIndex] = lowLevel;
        midFrequencyHistory[frequencyWriteIndex] = midLevel;
        highFrequencyHistory[frequencyWriteIndex] = highLevel;
        frequencyWriteIndex = (frequencyWriteIndex + 1) % FrequencyHistoryCount;
        return (OrderFrequencyHistory(lowFrequencyHistory), OrderFrequencyHistory(midFrequencyHistory), OrderFrequencyHistory(highFrequencyHistory));
    }

    private static float NormalizeFrequencyLevel(double power, int binCount)
    {
        if (binCount == 0) return 0;
        return Math.Clamp((float)Math.Sqrt(power / binCount) * 2.5f, 0, 1);
    }

    private float[] OrderFrequencyHistory(float[] history)
    {
        float[] ordered = new float[FrequencyHistoryCount];
        for (int index = 0; index < FrequencyHistoryCount; index++)
        {
            ordered[index] = history[(frequencyWriteIndex + index) % FrequencyHistoryCount];
        }
        return ordered;
    }

    private static AudioCurveSnapshot CreateSnapshot(
        float[] samples,
        float[] lowFrequencyLevels,
        float[] midFrequencyLevels,
        float[] highFrequencyLevels,
        float rmsPercent,
        float peakPercent,
        int sampleRate,
        string endpointPath,
        AudioCurveCaptureState state,
        string errorMessage) =>
        new(samples, lowFrequencyLevels, midFrequencyLevels, highFrequencyLevels, rmsPercent, peakPercent, sampleRate, endpointPath, state, errorMessage);

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint memory);

    [ComImport]
    [Guid(MmDeviceEnumeratorClsid)]
    private sealed class MmDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid(MmDeviceEnumeratorIid)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid(MmDeviceIid)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsContext, nint activationParams, out nint interfacePointer);
        [PreserveSig] int OpenPropertyStore(uint access, out nint propertyStore);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid(AudioClientIid)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closestMatch);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, out nint service);
    }

    [ComImport]
    [Guid(AudioCaptureClientIid)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    private readonly record struct WaveFormatInfo(
        ushort FormatTag,
        ushort Channels,
        int SampleRate,
        ushort BlockAlign,
        ushort BitsPerSample,
        bool IsFloat)
    {
        public static WaveFormatInfo Read(nint format)
        {
            ushort formatTag = (ushort)Marshal.ReadInt16(format, 0);
            ushort channels = (ushort)Marshal.ReadInt16(format, 2);
            int sampleRate = Marshal.ReadInt32(format, 4);
            ushort blockAlign = (ushort)Marshal.ReadInt16(format, 12);
            ushort bitsPerSample = (ushort)Marshal.ReadInt16(format, 14);
            bool isFloat = formatTag == WaveFormatIeeeFloat;
            if (formatTag == WaveFormatExtensible)
            {
                int subFormatData1 = Marshal.ReadInt32(format, 24);
                isFloat = subFormatData1 == WaveFormatIeeeFloat;
                formatTag = isFloat ? WaveFormatIeeeFloat : WaveFormatPcm;
            }

            if (formatTag != WaveFormatPcm && formatTag != WaveFormatIeeeFloat)
            {
                throw new NotSupportedException($"Unsupported WAVEFORMAT tag 0x{formatTag:X4}.");
            }

            return new WaveFormatInfo(formatTag, channels, sampleRate, blockAlign, bitsPerSample, isFloat);
        }
    }
}
