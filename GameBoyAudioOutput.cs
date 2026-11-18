using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using LEESIR.Cores.Gameboy;
using LEESIR.Cores.Nes;

namespace LEESIR;

/// <summary>
/// Small Win32 waveOut streaming sink for LEESIR's interleaved stereo PCM16 samples.
/// It deliberately lives in the Windows front end; emulator cores only produce PCM.
/// </summary>
internal sealed class GameBoyAudioOutput : IDisposable
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WhdrDone = 0x00000001;
    private const uint MmSysErrNoError = 0;
    private const int ChannelCount = 2;
    private const int BitsPerSample = 16;
    private const int FramesPerBuffer = 480; // 10 ms at 48 kHz.
    private const int BufferCount = 6;
    private const int PrebufferCount = 4; // Start with ~40 ms queued.

    private readonly IntPtr _waveOut;
    private readonly AudioBlock[] _blocks = new AudioBlock[BufferCount];
    private readonly short[] _scratch = new short[FramesPerBuffer * ChannelCount];
    private bool _primed;
    private bool _disposed;

    internal GameBoyAudioOutput(int sampleRate)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Step 11B waveOut presenter requires Windows.");
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var format = new WaveFormatEx
        {
            FormatTag = 1, // PCM
            Channels = ChannelCount,
            SamplesPerSec = (uint)sampleRate,
            BitsPerSample = BitsPerSample,
            BlockAlign = (ushort)(ChannelCount * (BitsPerSample / 8)),
        };
        format.AvgBytesPerSec = format.SamplesPerSec * format.BlockAlign;

        uint result = waveOutOpen(out IntPtr waveOut, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        _waveOut = waveOut;
        ThrowIfMmError(result, "Opening the Windows audio output");

        try
        {
            int headerSize = Marshal.SizeOf<WaveHeader>();
            int bufferBytes = _scratch.Length * sizeof(short);
            for (int i = 0; i < _blocks.Length; i++)
            {
                var block = new AudioBlock
                {
                    Data = Marshal.AllocHGlobal(bufferBytes),
                    Header = Marshal.AllocHGlobal(headerSize),
                };

                var header = new WaveHeader
                {
                    Data = block.Data,
                    BufferLength = (uint)bufferBytes,
                };
                Marshal.StructureToPtr(header, block.Header, false);
                _blocks[i] = block;
                ThrowIfMmError(
                    waveOutPrepareHeader(_waveOut, block.Header, (uint)headerSize),
                    "Preparing an audio buffer");
                block.Prepared = true;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int QueuedBuffers
    {
        get
        {
            ReclaimCompleted();
            int count = 0;
            foreach (AudioBlock block in _blocks)
                if (block.Submitted) count++;
            return count;
        }
    }

    internal int UnderrunCount { get; private set; }

    /// <summary>Moves generated Game Boy samples into Windows playback buffers.</summary>
    internal void Pump(GameBoyCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        PumpCore(() => core.AvailableAudioSampleFrames, core.ReadAudioSamples);
    }

    /// <summary>Moves generated NES samples into Windows playback buffers.</summary>
    internal void Pump(NesCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        PumpCore(() => core.AvailableAudioSampleFrames, core.ReadAudioSamples);
    }

    private delegate int SampleReader(Span<short> destination);

    private void PumpCore(Func<int> availableFrames, SampleReader readSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReclaimCompleted();

        if (!_primed)
        {
            int requiredFrames = FramesPerBuffer * PrebufferCount;
            if (availableFrames() < requiredFrames)
                return;

            for (int i = 0; i < PrebufferCount; i++)
            {
                if (!TrySubmitOne(availableFrames, readSamples))
                    break;
            }
            _primed = CountSubmitted() > 0;
            return;
        }

        while (availableFrames() >= FramesPerBuffer && TrySubmitOne(availableFrames, readSamples))
        {
            // Fill every currently free hardware buffer while enough PCM is available.
        }

        if (CountSubmitted() == 0)
        {
            UnderrunCount++;
            _primed = false;
        }
    }

    /// <summary>Stops queued Windows audio but keeps the device open for the next Run.</summary>
    internal void Reset()
    {
        if (_disposed || _waveOut == IntPtr.Zero)
            return;

        ThrowIfMmError(waveOutReset(_waveOut), "Resetting Windows audio output");
        foreach (AudioBlock block in _blocks)
            block.Submitted = false;
        _primed = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_waveOut != IntPtr.Zero)
            waveOutReset(_waveOut);

        int headerSize = Marshal.SizeOf<WaveHeader>();
        foreach (AudioBlock? block in _blocks)
        {
            if (block is null) continue;
            if (_waveOut != IntPtr.Zero && block.Prepared && block.Header != IntPtr.Zero)
                waveOutUnprepareHeader(_waveOut, block.Header, (uint)headerSize);
            if (block.Header != IntPtr.Zero)
                Marshal.FreeHGlobal(block.Header);
            if (block.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(block.Data);
        }

        if (_waveOut != IntPtr.Zero)
            waveOutClose(_waveOut);
    }

    private bool TrySubmitOne(Func<int> availableFrames, SampleReader readSamples)
    {
        AudioBlock? block = null;
        foreach (AudioBlock candidate in _blocks)
        {
            if (!candidate.Submitted)
            {
                block = candidate;
                break;
            }
        }
        if (block is null || availableFrames() < FramesPerBuffer)
            return false;

        int read = readSamples(_scratch);
        if (read != _scratch.Length)
            throw new InvalidOperationException($"The emulator core returned {read} sample values for a {_scratch.Length}-value Windows audio buffer.");

        Marshal.Copy(_scratch, 0, block.Data, _scratch.Length);
        uint result = waveOutWrite(_waveOut, block.Header, (uint)Marshal.SizeOf<WaveHeader>());
        ThrowIfMmError(result, "Submitting an audio buffer");
        block.Submitted = true;
        return true;
    }

    private void ReclaimCompleted()
    {
        if (_disposed) return;
        foreach (AudioBlock block in _blocks)
        {
            if (!block.Submitted) continue;
            WaveHeader header = Marshal.PtrToStructure<WaveHeader>(block.Header);
            if ((header.Flags & WhdrDone) != 0)
                block.Submitted = false;
        }
    }

    private int CountSubmitted()
    {
        int count = 0;
        foreach (AudioBlock block in _blocks)
            if (block.Submitted) count++;
        return count;
    }

    private static void ThrowIfMmError(uint result, string operation)
    {
        if (result == MmSysErrNoError) return;

        var message = new StringBuilder(256);
        if (waveOutGetErrorText(result, message, message.Capacity) != MmSysErrNoError)
            message.Append("Unknown multimedia-system error.");
        throw new Win32Exception((int)result, $"{operation} failed: {message}");
    }

    private sealed class AudioBlock
    {
        internal IntPtr Data;
        internal IntPtr Header;
        internal bool Prepared;
        internal bool Submitted;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        internal ushort FormatTag;
        internal ushort Channels;
        internal uint SamplesPerSec;
        internal uint AvgBytesPerSec;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        internal IntPtr Data;
        internal uint BufferLength;
        internal uint BytesRecorded;
        internal UIntPtr User;
        internal uint Flags;
        internal uint Loops;
        internal IntPtr Next;
        internal UIntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(
        out IntPtr waveOut,
        uint deviceId,
        ref WaveFormatEx format,
        IntPtr callback,
        IntPtr instance,
        uint flags);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr waveOut, IntPtr header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr waveOut, IntPtr header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr waveOut, IntPtr header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr waveOut);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr waveOut);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetErrorTextW")]
    private static extern uint waveOutGetErrorText(uint error, StringBuilder text, int textLength);
}
