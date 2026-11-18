using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LEESIR.Abstractions;
using LEESIR.Cores.Gameboy;
using LEESIR.Cores.Nes;
using LEESIR.Host;

namespace LEESIR;

// Keep the existing InitializeRomLoadingUi(); call in Form1's constructor.
public partial class Form1
{
    private const double GameBoyClockHz = 4_194_304.0;
    private const double NesClockHz = 1_789_773.0;
    private const int FrameBufferLength = GameBoyCore.VideoWidth * GameBoyCore.VideoHeight;
    private const int NesFrameBufferLength = NesCore.VideoWidth * NesCore.VideoHeight;
    private const int MaxStepFrameInstructions = 500_000;

    private readonly RomFileInspector _romInspector = new();
    private readonly RomFileLoader _romFileLoader = new();
    private readonly EmulationHost _emulationHost = CreateEmulationHost();
    private readonly CancellationTokenSource _romReadCancellation = new();
    private readonly System.Windows.Forms.Timer _emulationTimer = new() { Interval = 4 };
    private readonly Stopwatch _emulationClock = new();
    private readonly byte[] _videoFrame = new byte[FrameBufferLength];
    private readonly byte[] _nesVideoFrame = new byte[NesFrameBufferLength];
    private readonly HashSet<Keys> _pressedInputKeys = new();
    private GameBoyAudioOutput? _audioOutput;
    private string? _audioOutputError;

    private RomInspection? _currentInspection;
    private bool _romBusy;
    private bool _romUiClosed;
    private bool _emulationRunning;
    private double _cycleAccumulator;
    private ulong _lastPresentedFrame;

    private ToolStripButton _openRomButton = null!;
    private ToolStripButton _loadCartridgeButton = null!;
    private ToolStripButton _unloadCartridgeButton = null!;
    private ToolStripButton _runButton = null!;
    private ToolStripButton _pauseButton = null!;
    private ToolStripButton _stepFrameButton = null!;
    private ToolStripButton _saveStateButton = null!;
    private ToolStripButton _loadStateButton = null!;
    private ToolStripComboBox _romFormatChoice = null!;
    private ToolStripStatusLabel _romStatus = null!;
    private TextBox _romInformation = null!;
    private GameBoyDisplayControl _gameBoyDisplay = null!;
    private NesDisplayControl _nesDisplay = null!;

    private bool CanUpdateRomUi => !_romUiClosed && !IsDisposed && !Disposing;
    private GameBoyCore? ActiveGameBoyCore => _emulationHost.ActiveCore as GameBoyCore;
    private NesCore? ActiveNesCore => _emulationHost.ActiveCore as NesCore;

    private static EmulationHost CreateEmulationHost()
    {
        var host = new EmulationHost();
        host.RegisterCore(RomFormat.GameBoy, () => new GameBoyCore());
        host.RegisterCore(RomFormat.Nes, () => new NesCore());
        return host;
    }

    private void InitializeRomLoadingUi()
    {
        if (LicenseManager.UsageMode == LicenseUsageMode.Designtime) return;

        _romInformation = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 10),
            BackColor = SystemColors.Window
        };

        _gameBoyDisplay = new GameBoyDisplayControl { Dock = DockStyle.Fill };
        _nesDisplay = new NesDisplayControl { Dock = DockStyle.Fill, Visible = false };
        var displayHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 24, 24),
            Padding = new Padding(12)
        };
        displayHost.Controls.Add(_gameBoyDisplay);
        displayHost.Controls.Add(_nesDisplay);
        _gameBoyDisplay.BringToFront();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical
        };
        split.Panel1.Controls.Add(displayHost);
        split.Panel2.Controls.Add(CreateAssistantPanel());

        _openRomButton = new ToolStripButton("Open ROM");
        _openRomButton.Click += OpenRomForInspection;
        _loadCartridgeButton = new ToolStripButton("Load cartridge");
        _loadCartridgeButton.Click += LoadSelectedCartridge;
        _unloadCartridgeButton = new ToolStripButton("Unload");
        _unloadCartridgeButton.Click += UnloadCartridge;
        _runButton = new ToolStripButton("Run");
        _runButton.Click += StartEmulation;
        _pauseButton = new ToolStripButton("Pause");
        _pauseButton.Click += PauseEmulation;
        _stepFrameButton = new ToolStripButton("Step frame");
        _stepFrameButton.Click += StepOneFrame;
        _saveStateButton = new ToolStripButton("Save state");
        _saveStateButton.Click += SaveCurrentState;
        _loadStateButton = new ToolStripButton("Load state");
        _loadStateButton.Click += LoadCurrentState;

        _romFormatChoice = new ToolStripComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            AutoSize = false,
            Width = 190,
            AccessibleName = "ROM interpretation"
        };
        _romFormatChoice.Items.AddRange(new object[] { "Automatic", "Game Boy (manual)", "NES (manual)" });
        _romFormatChoice.SelectedIndex = 0;
        _romFormatChoice.SelectedIndexChanged += (_, _) => ShowRomInspection();

        var toolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        toolbar.Items.AddRange(new ToolStripItem[]
        {
            _openRomButton,
            _loadCartridgeButton,
            _unloadCartridgeButton,
            new ToolStripSeparator(),
            _runButton,
            _pauseButton,
            _stepFrameButton,
            new ToolStripSeparator(),
            _saveStateButton,
            _loadStateButton,
            new ToolStripSeparator(),
            new ToolStripLabel("Interpret as:"),
            _romFormatChoice
        });

        _romStatus = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        var statusBar = new StatusStrip { Dock = DockStyle.Bottom };
        statusBar.Items.Add(_romStatus);

        _emulationTimer.Tick += EmulationTimerTick;
        InitializeAudioOutput();

        KeyPreview = true;
        KeyDown += HandleEmulatorKeyDown;
        KeyUp += HandleEmulatorKeyUp;
        Deactivate += (_, _) => ReleaseAllInput();

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(statusBar);

        Shown += (_, _) =>
        {
            int maximumDistance = split.ClientSize.Width - split.SplitterWidth - 1;
            if (maximumDistance > 1)
                split.SplitterDistance = Math.Clamp(520, 1, maximumDistance);
        };

        ShowRomInspection();
    }

    private async void OpenRomForInspection(object? sender, EventArgs e)
    {
        if (_romBusy || !CanUpdateRomUi) return;
        using var picker = new OpenFileDialog
        {
            Title = "Open a ROM to inspect",
            Filter = "Supported ROM files (*.gb;*.gbc;*.nes;*.bin)|*.gb;*.gbc;*.nes;*.bin|Game Boy files (*.gb;*.gbc)|*.gb;*.gbc|NES files (*.nes)|*.nes|Binary files (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK || !CanUpdateRomUi) return;

        CancellationToken cancellationToken = _romReadCancellation.Token;
        SetRomBusy(true);
        _romStatus.Text = "Reading ROM header...";
        try
        {
            RomInspection inspection = await _romInspector.InspectAsync(picker.FileName, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanUpdateRomUi) return;
            _currentInspection = inspection;
            ShowRomInspection();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedRomError(exception))
        {
            if (!CanUpdateRomUi) return;
            _romStatus.Text = "Could not inspect the selected file. Previous results are preserved.";
            MessageBox.Show(this, exception.Message, "Open ROM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetRomBusy(false);
        }
    }

    private async void LoadSelectedCartridge(object? sender, EventArgs e)
    {
        if (_romBusy || _currentInspection is null || !CanUpdateRomUi) return;
        ReleaseAllInput();
        StopEmulation(updateDisplay: false);
        string filePath = _currentInspection.FilePath;
        RomFormat? manualFormat = SelectedManualFormat();
        CancellationToken cancellationToken = _romReadCancellation.Token;
        SetRomBusy(true);
        _romStatus.Text = "Reading cartridge data...";
        try
        {
            var image = await _romFileLoader.ReadAsync(filePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanUpdateRomUi) return;

            _currentInspection = image.Inspection;
            ShowRomInspection();
            _emulationHost.LoadRom(image, manualFormat);
            ResetAssistantCheatsForCartridgeChange();

            GameBoyCore? core = ActiveGameBoyCore;
            if (core is not null)
            {
                core.PrepareForExecutionWithoutBootRom();
                core.ClearAudioSamples();
                _audioOutput?.Reset();
                _nesDisplay.Visible = false;
                _gameBoyDisplay.Visible = true;
                _gameBoyDisplay.BringToFront();
                _lastPresentedFrame = core.CompletedVideoFrames;
                PresentFrame(core);
            }
            else if (ActiveNesCore is NesCore nesCore)
            {
                _audioOutput?.Reset();
                nesCore.ClearAudioSamples();
                _gameBoyDisplay.Visible = false;
                _nesDisplay.Visible = true;
                _nesDisplay.BringToFront();
                _lastPresentedFrame = nesCore.CompletedVideoFrames;
                PresentFrame(nesCore);
            }
            ShowRomInspection();
            AssistantOnCartridgeLoaded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedRomError(exception))
        {
            if (!CanUpdateRomUi) return;
            ShowRomInspection();
            var loaded = _emulationHost.LoadedCartridge;
            _romStatus.Text = loaded is null
                ? "Load failed. No cartridge is loaded."
                : $"Load failed. Still loaded: {Path.GetFileName(loaded.FilePath)}.";
            MessageBox.Show(this, exception.Message, "Load cartridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetRomBusy(false);
        }
    }

    private void UnloadCartridge(object? sender, EventArgs e)
    {
        if (_romBusy || !CanUpdateRomUi) return;
        try
        {
            ReleaseAllInput();
            StopEmulation(updateDisplay: false);
            _audioOutput?.Reset();
            _emulationHost.Unload();
            ResetAssistantCheatsForCartridgeChange();
            AssistantOnCartridgeUnloaded();
            _gameBoyDisplay.ClearFrame();
            _nesDisplay.ClearFrame();
            ShowRomInspection();
            _romStatus.Text = "Cartridge unloaded. The inspected file is still selected.";
        }
        catch (Exception exception) when (IsExpectedRomError(exception))
        {
            if (!CanUpdateRomUi) return;
            ShowRomInspection();
            _romStatus.Text = "Could not unload the cartridge.";
            MessageBox.Show(this, exception.Message, "Unload cartridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void StartEmulation(object? sender, EventArgs e)
    {
        GameBoyCore? gameBoy = ActiveGameBoyCore;
        NesCore? nes = ActiveNesCore;
        if (_romBusy || _emulationRunning || !CanUpdateRomUi || (gameBoy is null && nes is null)) return;

        string? fault = gameBoy?.IsCpuFaulted == true ? gameBoy.CpuFaultMessage
            : nes?.IsCpuFaulted == true ? nes.CpuFaultMessage
            : null;
        if (fault is not null)
        {
            MessageBox.Show(this, fault, "Run", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _audioOutput?.Reset();
        gameBoy?.ClearAudioSamples();
        nes?.ClearAudioSamples();
        _cycleAccumulator = 0;
        _emulationClock.Restart();
        _emulationRunning = true;
        _emulationTimer.Start();
        ShowRomInspection();
    }

    private void PauseEmulation(object? sender, EventArgs e) => StopEmulation(updateDisplay: true);

    private void StopEmulation(bool updateDisplay)
    {
        _emulationTimer.Stop();
        _emulationClock.Reset();
        _cycleAccumulator = 0;
        _emulationRunning = false;
        _audioOutput?.Reset();
        ActiveGameBoyCore?.ClearAudioSamples();
        ActiveNesCore?.ClearAudioSamples();
        if (updateDisplay && CanUpdateRomUi)
            ShowRomInspection();
    }

    private void EmulationTimerTick(object? sender, EventArgs e)
    {
        GameBoyCore? gameBoy = ActiveGameBoyCore;
        NesCore? nes = ActiveNesCore;
        if (!_emulationRunning || !CanUpdateRomUi || (gameBoy is null && nes is null))
        {
            StopEmulation(updateDisplay: true);
            return;
        }

        double elapsedSeconds = Math.Min(_emulationClock.Elapsed.TotalSeconds, 0.050);
        _emulationClock.Restart();
        double clockHz = gameBoy is not null ? GameBoyClockHz : NesClockHz;
        _cycleAccumulator += elapsedSeconds * clockHz;

        double maxDebt = gameBoy is not null
            ? 456.0 * 154.0 * 2.0
            : 29_781.0 * 2.0;
        if (_cycleAccumulator > maxDebt)
            _cycleAccumulator = maxDebt;

        try
        {
            int instructionGuard = 100_000;
            while (_cycleAccumulator >= 1.0 && instructionGuard-- > 0)
            {
                int cycles = gameBoy is not null
                    ? gameBoy.StepInstruction()
                    : nes!.StepInstruction();
                _cycleAccumulator -= cycles;

                ulong completed = gameBoy?.CompletedVideoFrames ?? nes!.CompletedVideoFrames;
                if (completed != _lastPresentedFrame)
                {
                    _lastPresentedFrame = completed;
                    if (gameBoy is not null)
                        PresentFrame(gameBoy);
                    else
                        PresentFrame(nes!);
                }
            }

            ApplyAssistantCheats();

            if (gameBoy is not null)
                PumpAudio(gameBoy);
            else if (nes is not null)
                PumpAudio(nes);

            if (instructionGuard <= 0)
                throw new InvalidOperationException("The UI execution guard was reached before the current real-time cycle budget completed.");
        }
        catch (Exception exception) when (IsExpectedRomError(exception) || exception is CpuExecutionException || exception is NesCpuExecutionException)
        {
            StopEmulation(updateDisplay: true);
            MessageBox.Show(this, exception.Message, "CPU execution stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StepOneFrame(object? sender, EventArgs e)
    {
        GameBoyCore? gameBoy = ActiveGameBoyCore;
        NesCore? nes = ActiveNesCore;
        if (_romBusy || _emulationRunning || !CanUpdateRomUi || (gameBoy is null && nes is null)) return;

        ulong current = gameBoy?.CompletedVideoFrames ?? nes!.CompletedVideoFrames;
        ulong target = current + 1;

        try
        {
            int instructions = 0;
            while ((gameBoy?.CompletedVideoFrames ?? nes!.CompletedVideoFrames) < target
                && instructions++ < MaxStepFrameInstructions)
            {
                if (gameBoy is not null) gameBoy.StepInstruction();
                else nes!.StepInstruction();
            }

            if ((gameBoy?.CompletedVideoFrames ?? nes!.CompletedVideoFrames) < target)
                throw new InvalidOperationException("No video frame completed within the Step frame instruction guard.");

            ApplyAssistantCheats();
            _lastPresentedFrame = target;
            if (gameBoy is not null)
            {
                PresentFrame(gameBoy);
                gameBoy.ClearAudioSamples();
            }
            else
            {
                PresentFrame(nes!);
                nes!.ClearAudioSamples();
            }
            ShowRomInspection();
        }
        catch (Exception exception) when (IsExpectedRomError(exception) || exception is CpuExecutionException || exception is NesCpuExecutionException)
        {
            StopEmulation(updateDisplay: true);
            MessageBox.Show(this, exception.Message, "Step frame", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }


    private void SaveCurrentState(object? sender, EventArgs e)
    {
        if (_romBusy || _emulationRunning || ActiveGameBoyCore is null || !CanUpdateRomUi) return;

        try
        {
            if (!_emulationHost.SaveActiveState())
                throw new InvalidOperationException("The active core does not support save states.");

            ShowRomInspection();
            _romStatus.Text = $"Save state written: {Path.GetFileName(_emulationHost.SaveStatePath)}";
        }
        catch (Exception exception) when (IsExpectedRomError(exception))
        {
            MessageBox.Show(this, exception.Message, "Save state", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadCurrentState(object? sender, EventArgs e)
    {
        if (_romBusy || _emulationRunning || ActiveGameBoyCore is null || !CanUpdateRomUi) return;

        try
        {
            ReleaseAllInput();
            StopEmulation(updateDisplay: false);
            _audioOutput?.Reset();

            if (!_emulationHost.LoadActiveState())
                throw new InvalidOperationException("The active core does not support save states.");

            GameBoyCore core = ActiveGameBoyCore
                ?? throw new InvalidOperationException("The Game Boy core was not available after state restoration.");
            core.ReleaseAllButtons();
            core.ClearAudioSamples();
            _cycleAccumulator = 0;
            _lastPresentedFrame = core.CompletedVideoFrames;
            PresentFrame(core);
            ShowRomInspection();
            _romStatus.Text = $"Save state restored: {Path.GetFileName(_emulationHost.SaveStatePath)}";
        }
        catch (Exception exception) when (IsExpectedRomError(exception))
        {
            MessageBox.Show(this, exception.Message, "Load state", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void HandleEmulatorKeyDown(object? sender, KeyEventArgs e)
    {
        if (AssistantIsTyping) return;
        if (!IsEmulatorKey(e.KeyCode)) return;
        _pressedInputKeys.Add(e.KeyCode);
        RefreshAllInputButtons();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void HandleEmulatorKeyUp(object? sender, KeyEventArgs e)
    {
        if (AssistantIsTyping) return;
        if (!IsEmulatorKey(e.KeyCode)) return;
        _pressedInputKeys.Remove(e.KeyCode);
        RefreshAllInputButtons();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void RefreshAllInputButtons()
    {
        bool up = Pressed(Keys.Up, Keys.W);
        bool down = Pressed(Keys.Down, Keys.S);
        bool left = Pressed(Keys.Left, Keys.A);
        bool right = Pressed(Keys.Right, Keys.D);
        bool a = Pressed(Keys.Z);
        bool b = Pressed(Keys.X);
        bool start = Pressed(Keys.Enter);
        bool select = Pressed(Keys.LShiftKey, Keys.RShiftKey);

        if (ActiveGameBoyCore is GameBoyCore gb)
        {
            gb.SetButtonState(GameBoyButton.Up, up);
            gb.SetButtonState(GameBoyButton.Down, down);
            gb.SetButtonState(GameBoyButton.Left, left);
            gb.SetButtonState(GameBoyButton.Right, right);
            gb.SetButtonState(GameBoyButton.A, a);
            gb.SetButtonState(GameBoyButton.B, b);
            gb.SetButtonState(GameBoyButton.Start, start);
            gb.SetButtonState(GameBoyButton.Select, select);
        }

        if (ActiveNesCore is NesCore nes)
        {
            nes.SetButtonState(NesButton.Up, up);
            nes.SetButtonState(NesButton.Down, down);
            nes.SetButtonState(NesButton.Left, left);
            nes.SetButtonState(NesButton.Right, right);
            nes.SetButtonState(NesButton.A, a);
            nes.SetButtonState(NesButton.B, b);
            nes.SetButtonState(NesButton.Start, start);
            nes.SetButtonState(NesButton.Select, select);
        }
    }

    private bool Pressed(params Keys[] keys)
    {
        foreach (Keys key in keys)
        {
            if (_pressedInputKeys.Contains(key)) return true;
        }
        return false;
    }

    private void ReleaseAllInput()
    {
        _pressedInputKeys.Clear();
        ActiveGameBoyCore?.ReleaseAllButtons();
        ActiveNesCore?.ReleaseAllButtons();
    }

    private static bool IsEmulatorKey(Keys key) => key is
        Keys.Up or Keys.W or Keys.Down or Keys.S or Keys.Left or Keys.A or
        Keys.Right or Keys.D or Keys.Z or Keys.X or Keys.Enter or
        Keys.LShiftKey or Keys.RShiftKey;

    private void InitializeAudioOutput()
    {
        try
        {
            _audioOutput = new GameBoyAudioOutput(GameBoyCore.AudioSampleRate);
            _audioOutputError = null;
        }
        catch (Exception exception)
        {
            _audioOutput = null;
            _audioOutputError = exception.Message;
        }
    }

    private void PumpAudio(GameBoyCore core)
    {
        if (_audioOutput is null) return;
        try
        {
            _audioOutput.Pump(core);
        }
        catch (Exception exception)
        {
            _audioOutputError = exception.Message;
            _audioOutput.Dispose();
            _audioOutput = null;
            core.ClearAudioSamples();
        }
    }

    private void PumpAudio(NesCore core)
    {
        if (_audioOutput is null) return;
        try
        {
            _audioOutput.Pump(core);
        }
        catch (Exception exception)
        {
            _audioOutputError = exception.Message;
            _audioOutput.Dispose();
            _audioOutput = null;
            core.ClearAudioSamples();
        }
    }

    private void PresentFrame(GameBoyCore core)
    {
        core.CopyVideoFrame(_videoFrame);
        _gameBoyDisplay.SetFrame(_videoFrame);
        _romStatus.Text = $"Frame {core.CompletedVideoFrames:N0} | PC 0x{core.Registers.PC:X4} | {core.TotalTCycles:N0} T-cycles | Audio {_audioOutput?.QueuedBuffers ?? 0} buffers";
    }

    private void PresentFrame(NesCore core)
    {
        core.CopyVideoFrame(_nesVideoFrame);
        _nesDisplay.SetFrame(_nesVideoFrame);
        _romStatus.Text = $"NES Frame {core.CompletedVideoFrames:N0} | PC 0x{core.Registers.PC:X4} | {core.TotalCpuCycles:N0} CPU cycles | Audio {_audioOutput?.QueuedBuffers ?? 0} buffers";
    }

    private void SetRomBusy(bool busy)
    {
        _romBusy = busy;
        UpdateRomActions();
    }

    private void UpdateRomActions()
    {
        if (!CanUpdateRomUi) return;
        GameBoyCore? gameBoy = ActiveGameBoyCore;
        NesCore? nes = ActiveNesCore;
        bool activeRunnableCore = gameBoy is not null || nes is not null;
        bool activeFaulted = gameBoy?.IsCpuFaulted == true || nes?.IsCpuFaulted == true;

        _openRomButton.Enabled = !_romBusy && !_emulationRunning;
        _loadCartridgeButton.Enabled = !_romBusy && !_emulationRunning && _currentInspection is not null;
        _unloadCartridgeButton.Enabled = !_romBusy && !_emulationRunning && _emulationHost.LoadedCartridge is not null;
        _runButton.Enabled = !_romBusy && !_emulationRunning && activeRunnableCore && !activeFaulted;
        _pauseButton.Enabled = !_romBusy && _emulationRunning;
        _stepFrameButton.Enabled = !_romBusy && !_emulationRunning && activeRunnableCore && !activeFaulted;
        _saveStateButton.Enabled = !_romBusy && !_emulationRunning && gameBoy is not null && !gameBoy.IsCpuFaulted;
        _loadStateButton.Enabled = !_romBusy && !_emulationRunning && gameBoy is not null;
        _romFormatChoice.Enabled = !_romBusy && !_emulationRunning;
    }

    private void ShowRomInspection()
    {
        if (!CanUpdateRomUi) return;
        var text = new StringBuilder();
        text.AppendLine("LEESIR - unified multi-system ROM inspection and cartridge loading");
        text.AppendLine();
        text.AppendLine("LOADED CARTRIDGE");
        var loaded = _emulationHost.LoadedCartridge;
        GameBoyCore? core = ActiveGameBoyCore;
        NesCore? nesCore = ActiveNesCore;
        if (loaded is null)
        {
            text.AppendLine("No cartridge is loaded into a core.");
        }
        else
        {
            text.AppendLine($"Core: {loaded.CoreName}");
            text.AppendLine($"Loaded file: {Path.GetFileName(loaded.FilePath)}");
            text.AppendLine($"Loaded bytes: {loaded.ByteCount:N0}");
            if (core is not null)
            {
                string state = core.IsCpuFaulted ? "Faulted" : _emulationRunning ? "Running" : "Paused / ready";
                text.AppendLine($"Execution: {state}");
                text.AppendLine($"PC: 0x{core.Registers.PC:X4}");
                text.AppendLine($"T-cycles: {core.TotalTCycles:N0}");
                text.AppendLine($"Completed video frames: {core.CompletedVideoFrames:N0}");
                text.AppendLine($"PPU: LY {core.CurrentVideoLine} | mode {core.CurrentVideoMode}");
                text.AppendLine($"APU: {(core.AudioPowered ? "powered" : "off")} | buffered {core.AvailableAudioSampleFrames:N0} sample frames");
                if (_audioOutput is not null)
                    text.AppendLine($"Windows audio: active | queued {_audioOutput.QueuedBuffers} buffers | underruns {_audioOutput.UnderrunCount}");
                else
                    text.AppendLine($"Windows audio: unavailable{(_audioOutputError is null ? string.Empty : $" - {_audioOutputError}")}");
                if (core.CartridgeHasBattery)
                {
                    text.AppendLine($"Battery RAM: {core.CartridgeRamByteCount:N0} bytes");
                    text.AppendLine($"Battery save: {_emulationHost.BatterySavePath ?? Path.ChangeExtension(loaded.FilePath, ".sav")}");
                    text.AppendLine($"Battery save loaded: {(_emulationHost.BatterySaveLoadedFromDisk ? "yes" : "no / new save")}");
                }
                string statePath = _emulationHost.SaveStatePath ?? Path.ChangeExtension(loaded.FilePath, ".state");
                text.AppendLine($"Save state: {statePath}");
                text.AppendLine($"Save state exists: {(File.Exists(statePath) ? "yes" : "no")}");
                if (core.IsCpuFaulted)
                    text.AppendLine($"CPU fault: {core.CpuFaultMessage}");
            }
            else if (nesCore is not null)
            {
                string state = nesCore.IsCpuFaulted ? "Faulted" : _emulationRunning ? "Running" : "Paused / ready";
                text.AppendLine($"Execution: {state}");
                text.AppendLine($"PC: 0x{nesCore.Registers.PC:X4}");
                text.AppendLine($"CPU cycles: {nesCore.TotalCpuCycles:N0}");
                text.AppendLine($"Completed video frames: {nesCore.CompletedVideoFrames:N0}");
                text.AppendLine($"PPU: scanline {nesCore.CurrentPpuScanline} | dot {nesCore.CurrentPpuDot}");
                text.AppendLine($"Mapper: {nesCore.MapperNumber} ({nesCore.MapperName})");
                text.AppendLine($"PRG-ROM: {nesCore.PrgRomByteCount:N0} bytes");
                text.AppendLine($"CHR memory: {nesCore.ChrByteCount:N0} bytes ({(nesCore.UsesChrRam ? "CHR-RAM" : "CHR-ROM")})");
                text.AppendLine($"Mirroring: {nesCore.Mirroring}");
                text.AppendLine($"APU: active channel mask 0x{nesCore.ActiveAudioChannels:X2} | buffered {nesCore.AvailableAudioSampleFrames:N0} sample frames");
                if (_audioOutput is not null)
                    text.AppendLine($"Windows audio: active | queued {_audioOutput.QueuedBuffers} buffers | underruns {_audioOutput.UnderrunCount}");
                else
                    text.AppendLine($"Windows audio: unavailable{(_audioOutputError is null ? string.Empty : $" - {_audioOutputError}")}");
                if (nesCore.IsCpuFaulted)
                    text.AppendLine($"CPU fault: {nesCore.CpuFaultMessage}");
            }
        }

        text.AppendLine();
        text.AppendLine("INSPECTED FILE");
        if (_currentInspection is null)
        {
            text.AppendLine("Choose Open ROM to inspect a raw .gb, .gbc, .nes or .bin file.");
            text.AppendLine("LEESIR automatically detects Game Boy and NES images; the NES core supports Mapper 0, 2 and 3 cartridges in Step 14F.");
        }
        else
        {
            _currentInspection = _currentInspection with
            {
                ManualFormat = SelectedManualFormat()
            };
            RomInspection inspection = _currentInspection;
            RomDetectionResult automatic = inspection.AutomaticDetection;
            text.AppendLine($"File: {Path.GetFileName(inspection.FilePath)}");
            text.AppendLine($"Path: {inspection.FilePath}");
            text.AppendLine($"File length: {inspection.FileLength:N0} bytes");
            text.AppendLine();
            text.AppendLine($"Automatic format: {FormatName(automatic.DetectedFormat)}");
            text.AppendLine($"Automatic confidence: {automatic.Confidence}");
            text.AppendLine(automatic.Summary);
            text.AppendLine($"Selected interpretation: {FormatName(inspection.EffectiveFormat)}"
                + (inspection.IsManualOverride ? " (manual)" : " (automatic)"));
            text.AppendLine();

            if (inspection.EffectiveFormat is RomFormat.GameBoy or RomFormat.Nes)
            {
                text.AppendLine(inspection.EffectiveFormat == RomFormat.GameBoy
                    ? "GAME BOY HEADER INTERPRETATION"
                    : "NES / iNES HEADER INTERPRETATION");
                foreach (var field in automatic.HeaderFields)
                    text.AppendLine($"{field.Key}: {field.Value}");
                text.AppendLine();
                text.AppendLine("FINDINGS");
                if (automatic.Issues.Count == 0)
                    text.AppendLine("No issues found by the header checks implemented so far.");
                foreach (RomIssue issue in automatic.Issues)
                    text.AppendLine($"[{issue.Severity}] {issue.Code}: {issue.Message}");
            }
        }

        text.AppendLine();
        text.AppendLine("VIDEO: Game Boy 160x144 and NES 256x240 framebuffers use nearest-neighbour scaling.");
        text.AppendLine("CONTROLS: Arrow keys or WASD = D-pad | Z = A | X = B | Enter = Start | Shift = Select.");
        text.AppendLine("AUDIO: Game Boy and NES APU PCM are streamed to Windows waveOut at 48 kHz stereo PCM16.");
        text.AppendLine("SAVE STATE: Game Boy Save state / Load state use a ROM-specific .state sidecar file.");
        text.AppendLine("NES: Mapper 0/NROM, Mapper 2/UxROM and Mapper 3/CNROM are active with CPU, PPU/video, controller 1 and APU audio.");
        _romInformation.Text = text.ToString();

        if (!_emulationRunning)
        {
            _romStatus.Text = core is not null
                ? $"Ready | PC 0x{core.Registers.PC:X4} | Frame {core.CompletedVideoFrames:N0}"
                : nesCore is not null
                    ? $"NES ready | PC 0x{nesCore.Registers.PC:X4} | Frame {nesCore.CompletedVideoFrames:N0}"
                    : _currentInspection is not null
                        ? $"Inspected {Path.GetFileName(_currentInspection.FilePath)} | {_currentInspection.AutomaticDetection.Confidence} automatic confidence"
                        : "No ROM selected";
        }
        UpdateRomActions();
    }

    private static bool IsExpectedRomError(Exception exception) => exception is
        IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or InvalidOperationException;

    private RomFormat? SelectedManualFormat() => _romFormatChoice.SelectedIndex switch
    {
        1 => RomFormat.GameBoy,
        2 => RomFormat.Nes,
        _ => null
    };

    private static string FormatName(RomFormat format) => format switch
    {
        RomFormat.GameBoy => "Game Boy-family cartridge",
        RomFormat.Nes => "Nintendo Entertainment System cartridge",
        _ => "Unknown"
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!e.Cancel)
        {
            try
            {
                _emulationHost.SaveActiveBattery();
            }
            catch (Exception exception) when (IsExpectedRomError(exception))
            {
                e.Cancel = true;
                MessageBox.Show(
                    this,
                    "LEESIR could not write the battery save file. The application will remain open so you can resolve the problem or copy your data.\r\n\r\n" + exception.Message,
                    "Battery save",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _romUiClosed = true;
        ReleaseAllInput();
        _emulationTimer.Stop();
        _emulationTimer.Dispose();
        _romReadCancellation.Cancel();
        _romReadCancellation.Dispose();
        try
        {
            _audioOutput?.Dispose();
            _audioOutput = null;
            _emulationHost.Dispose();
        }
        finally
        {
            base.OnFormClosed(e);
        }
    }
}
