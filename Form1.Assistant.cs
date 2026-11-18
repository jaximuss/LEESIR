using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LEESIR.Cores.Gameboy;
using LEESIR.Cores.Nes;

namespace LEESIR;

/// <summary>
/// Local, deterministic conversational control layer for LEESIR.
/// This is deliberately not a cloud LLM: commands are parsed locally so emulator
/// control continues to work offline and cannot silently send ROM/game data away.
/// </summary>
public partial class Form1
{
    private sealed record MemoryCheat(ushort Address, byte Value, string Label);

    private readonly List<MemoryCheat> _assistantCheats = new();
    private TabControl? _assistantTabs;
    private TabPage? _assistantPage;
    private TabPage? _detailsPage;
    private RichTextBox? _assistantTranscript;
    private TextBox? _assistantInput;
    private Button? _assistantSendButton;

    private bool AssistantIsTyping => _assistantInput?.Focused == true;

    private Control CreateAssistantPanel()
    {
        _assistantTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9.5f)
        };

        _assistantPage = new TabPage("LEESIR Assistant") { Padding = new Padding(8) };
        _detailsPage = new TabPage("ROM Details") { Padding = new Padding(4) };

        _assistantTranscript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            DetectUrls = false,
            BackColor = Color.FromArgb(248, 249, 251),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5f),
            TabStop = false
        };

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
            Text = "Examples: pause | run | status | what is DMA? | read 0xC000 | poke 0xC000 0xFF | cheat add 0xC000 0xFF | cheat list | cheat clear"
        };

        _assistantInput = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10f),
            PlaceholderText = "Ask LEESIR or enter a command..."
        };
        _assistantInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            ExecuteAssistantInput();
        };

        _assistantSendButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Send",
            AutoSize = true,
            MinimumSize = new Size(72, 30)
        };
        _assistantSendButton.Click += (_, _) => ExecuteAssistantInput();

        var inputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputRow.Controls.Add(_assistantInput, 0, 0);
        inputRow.Controls.Add(_assistantSendButton, 1, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.Controls.Add(_assistantTranscript, 0, 0);
        layout.Controls.Add(hint, 0, 1);
        layout.Controls.Add(inputRow, 0, 2);
        _assistantPage.Controls.Add(layout);

        _romInformation.Dock = DockStyle.Fill;
        _detailsPage.Controls.Add(_romInformation);

        _assistantTabs.TabPages.Add(_assistantPage);
        _assistantTabs.TabPages.Add(_detailsPage);
        _assistantTabs.SelectedTab = _assistantPage;

        AppendAssistant("LEESIR", "Local assistant ready. I can control emulation, explain common emulator terms, inspect memory and maintain simple RAM freeze cheats. Type 'help' for commands.");
        AppendAssistant("LEESIR", "Cheat note: this version uses raw memory addresses, not encoded Game Genie/Action Replay codes. Cheats are cleared when a cartridge changes.");
        return _assistantTabs;
    }

    private void ExecuteAssistantInput()
    {
        if (_assistantInput is null) return;
        string input = _assistantInput.Text.Trim();
        if (input.Length == 0) return;
        _assistantInput.Clear();
        AppendAssistant("You", input);

        try
        {
            string reply = ProcessAssistantCommand(input);
            AppendAssistant("LEESIR", reply);
        }
        catch (Exception ex)
        {
            AppendAssistant("LEESIR", "Command failed: " + ex.Message);
        }
    }

    private string ProcessAssistantCommand(string input)
    {
        string trimmed = input.Trim();
        string lower = trimmed.ToLowerInvariant();

        if (lower is "help" or "?" || lower.Contains("what can you do"))
            return AssistantHelp();

        if (TryKnowledgeReply(lower, out string? knowledge))
            return knowledge!;

        if (Regex.IsMatch(lower, @"\b(pause|stop)\b(?:\s+the)?(?:\s+game|\s+emulation)?\b"))
        {
            if (!_emulationRunning) return "The emulator is already paused.";
            StopEmulation(updateDisplay: true);
            return "Paused the active game.";
        }

        if (Regex.IsMatch(lower, @"\b(run|resume|continue|play)\b(?:\s+the)?(?:\s+game|\s+emulation)?\b"))
        {
            if (_emulationRunning) return "The emulator is already running.";
            if (ActiveGameBoyCore is null && ActiveNesCore is null) return "Load a Game Boy or NES cartridge first.";
            StartEmulation(null, EventArgs.Empty);
            return _emulationRunning ? "Resumed the active game." : "The game could not be started; check the ROM details/status for a CPU fault.";
        }

        if (lower is "step" or "step frame" || lower.Contains("step one frame"))
        {
            if (_emulationRunning) return "Pause the game before stepping a frame.";
            if (ActiveGameBoyCore is null && ActiveNesCore is null) return "Load a cartridge first.";
            StepOneFrame(null, EventArgs.Empty);
            return "Advanced one video frame.";
        }

        if (lower.Contains("status") || lower.Contains("what is running") || lower.Contains("what game") || lower.Contains("which core"))
            return BuildAssistantStatus();

        if (lower.Contains("rom details") || lower == "details" || lower == "show details")
        {
            if (_assistantTabs is not null && _detailsPage is not null) _assistantTabs.SelectedTab = _detailsPage;
            return "Opened the ROM Details tab.";
        }

        if (lower is "assistant" or "show assistant")
        {
            if (_assistantTabs is not null && _assistantPage is not null) _assistantTabs.SelectedTab = _assistantPage;
            return "Assistant tab selected.";
        }

        if (lower is "cheat list" or "list cheats" or "show cheats")
            return BuildCheatList();

        if (lower is "cheat clear" or "clear cheats" or "remove all cheats")
        {
            int count = _assistantCheats.Count;
            _assistantCheats.Clear();
            return count == 0 ? "There were no active RAM freeze cheats." : $"Cleared {count} RAM freeze cheat(s).";
        }

        Match remove = Regex.Match(lower, @"^(?:cheat\s+remove|remove\s+cheat)\s+(\d+)$");
        if (remove.Success)
        {
            int index = int.Parse(remove.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
            if (index < 0 || index >= _assistantCheats.Count) return "That cheat number does not exist. Use 'cheat list' first.";
            MemoryCheat removed = _assistantCheats[index];
            _assistantCheats.RemoveAt(index);
            return $"Removed cheat {index + 1}: {removed.Label}.";
        }

        Match freeze = Regex.Match(trimmed, @"^(?:cheat\s+add|freeze)\s+([0-9A-Fa-fxX$]+)\s+(?:at\s+)?([0-9A-Fa-fxX$]+)(?:\s+(.+))?$", RegexOptions.IgnoreCase);
        if (freeze.Success)
        {
            if (!TryParseHexUShort(freeze.Groups[1].Value, out ushort address) || !TryParseHexByte(freeze.Groups[2].Value, out byte value))
                return "Use hexadecimal addresses/values, for example: cheat add 0xC000 0xFF.";
            if (!IsSafeFreezeAddress(address, out string reason)) return reason;
            string label = freeze.Groups[3].Success ? freeze.Groups[3].Value.Trim() : $"Freeze 0x{address:X4} = 0x{value:X2}";
            _assistantCheats.Add(new MemoryCheat(address, value, label));
            TryWriteActiveMemory(address, value, out _);
            return $"Added cheat {_assistantCheats.Count}: {label}. LEESIR will rewrite 0x{address:X4} to 0x{value:X2} while the game runs.";
        }

        Match read = Regex.Match(trimmed, @"^(?:read|peek)(?:\s+memory)?\s+([0-9A-Fa-fxX$]+)$", RegexOptions.IgnoreCase);
        if (read.Success)
        {
            if (!TryParseHexUShort(read.Groups[1].Value, out ushort address)) return "Example: read 0xC000";
            return TryReadActiveMemory(address, out byte value, out string error)
                ? $"Memory 0x{address:X4} = 0x{value:X2} ({value})."
                : error;
        }

        Match poke = Regex.Match(trimmed, @"^(?:poke|write|set)(?:\s+memory)?\s+([0-9A-Fa-fxX$]+)\s+(?:to\s+)?([0-9A-Fa-fxX$]+)$", RegexOptions.IgnoreCase);
        if (poke.Success)
        {
            if (!TryParseHexUShort(poke.Groups[1].Value, out ushort address) || !TryParseHexByte(poke.Groups[2].Value, out byte value))
                return "Example: poke 0xC000 0xFF";
            if (!TryWriteActiveMemory(address, value, out string error)) return error;
            return $"Wrote 0x{value:X2} to 0x{address:X4}. This is a one-time raw bus write; use 'cheat add' to freeze a RAM value.";
        }

        return "I do not understand that command yet. Type 'help'. I am intentionally a small local command assistant rather than a general cloud chatbot.";
    }

    private string AssistantHelp() =>
        "Commands:\r\n" +
        "  pause / pause the game\r\n" +
        "  run / resume the game\r\n" +
        "  step frame\r\n" +
        "  status\r\n" +
        "  details\r\n" +
        "  read 0xC000\r\n" +
        "  poke 0xC000 0xFF        (one-time raw memory write)\r\n" +
        "  cheat add 0xC000 0xFF   (freeze a safe RAM address)\r\n" +
        "  cheat list\r\n" +
        "  cheat remove 1\r\n" +
        "  cheat clear\r\n" +
        "You can also ask: what is MBC1/APU/PPU/DMA/NROM/UxROM/CNROM/MMC3?";

    private string BuildAssistantStatus()
    {
        if (ActiveGameBoyCore is GameBoyCore gb)
            return $"System: Game Boy | State: {(_emulationRunning ? "Running" : "Paused/ready")} | PC: 0x{gb.Registers.PC:X4} | Frame: {gb.CompletedVideoFrames:N0} | Cartridge type: 0x{gb.CartridgeType:X2} | Active cheats: {_assistantCheats.Count}.";
        if (ActiveNesCore is NesCore nes)
            return $"System: NES | State: {(_emulationRunning ? "Running" : "Paused/ready")} | PC: 0x{nes.Registers.PC:X4} | Frame: {nes.CompletedVideoFrames:N0} | Mapper: {nes.MapperNumber} ({nes.MapperName}) | Active cheats: {_assistantCheats.Count}.";
        return "No cartridge is currently loaded. Open and load a Game Boy or NES ROM first.";
    }

    private string BuildCheatList()
    {
        if (_assistantCheats.Count == 0) return "No RAM freeze cheats are active.";
        var text = new StringBuilder("Active RAM freeze cheats:\r\n");
        for (int i = 0; i < _assistantCheats.Count; i++)
        {
            MemoryCheat c = _assistantCheats[i];
            text.AppendLine($"  {i + 1}. 0x{c.Address:X4} = 0x{c.Value:X2} | {c.Label}");
        }
        return text.ToString().TrimEnd();
    }

    private bool TryKnowledgeReply(string lower, out string? reply)
    {
        reply = null;
        if (!lower.Contains("what is") && !lower.Contains("explain") && !lower.Contains("define")) return false;

        var knowledge = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mbc1"] = "MBC1 is a Game Boy Memory Bank Controller. It switches ROM/RAM banks so cartridges can expose more data than the CPU cartridge window can address at one time. LEESIR supports MBC1.",
            ["mbc2"] = "MBC2 is a Game Boy controller with up to 16 switchable ROM banks and 512 x 4-bit built-in RAM. It is future work in LEESIR.",
            ["mbc3"] = "MBC3 is a later Game Boy bank controller; timer variants add a battery-backed real-time clock. It is not implemented yet.",
            ["mbc5"] = "MBC5 is a later Game Boy controller with wider ROM-bank selection and larger cartridge capacity. It is not implemented yet.",
            ["apu"] = "APU means Audio Processing Unit. LEESIR has separate Game Boy and NES APU implementations that produce PCM audio for the Windows output layer.",
            ["ppu"] = "PPU means Picture Processing Unit. It is the console graphics subsystem responsible for rendering backgrounds/sprites and display timing.",
            ["dma"] = "DMA means Direct Memory Access. It transfers blocks of memory without ordinary byte-by-byte CPU instructions; LEESIR implements sprite/OAM DMA on both current systems.",
            ["nrom"] = "NROM is NES Mapper 0: fixed PRG and CHR with no bank switching. Super Mario Bros. is a key LEESIR NROM test.",
            ["uxrom"] = "UxROM is NES Mapper 2: a switchable 16 KiB PRG bank plus a fixed final PRG bank. LEESIR supports it.",
            ["cnrom"] = "CNROM is NES Mapper 3: fixed PRG with switchable 8 KiB CHR-ROM. LEESIR supports it.",
            ["mmc3"] = "MMC3 is NES Mapper 4, an advanced mapper with PRG/CHR banking and IRQ behaviour. Mega Man 5 uses Mapper 4; LEESIR currently detects and rejects it safely."
        };

        foreach ((string key, string value) in knowledge)
        {
            if (lower.Contains(key)) { reply = value; return true; }
        }
        return false;
    }

    private static bool TryParseHexUShort(string token, out ushort value)
    {
        string s = token.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (s.StartsWith('$')) s = s[1..];
        return ushort.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseHexByte(string token, out byte value)
    {
        string s = token.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (s.StartsWith('$')) s = s[1..];
        return byte.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private bool TryReadActiveMemory(ushort address, out byte value, out string error)
    {
        try
        {
            if (ActiveGameBoyCore is GameBoyCore gb) { value = gb.ReadByte(address); error = string.Empty; return true; }
            if (ActiveNesCore is NesCore nes) { value = nes.ReadByte(address); error = string.Empty; return true; }
            value = 0; error = "No active core is loaded."; return false;
        }
        catch (Exception ex)
        {
            value = 0; error = "Memory read failed: " + ex.Message; return false;
        }
    }

    private bool TryWriteActiveMemory(ushort address, byte value, out string error)
    {
        try
        {
            if (ActiveGameBoyCore is GameBoyCore gb) { gb.WriteByte(address, value); error = string.Empty; return true; }
            if (ActiveNesCore is NesCore nes) { nes.WriteByte(address, value); error = string.Empty; return true; }
            error = "No active core is loaded."; return false;
        }
        catch (Exception ex)
        {
            error = "Memory write failed: " + ex.Message; return false;
        }
    }

    private bool IsSafeFreezeAddress(ushort address, out string reason)
    {
        if (ActiveGameBoyCore is not null)
        {
            bool safe = (address >= 0xA000 && address <= 0xFDFF) || (address >= 0xFF80 && address <= 0xFFFE);
            reason = safe ? string.Empty : "For Game Boy, freeze cheats are restricted to cartridge/work/echo/high RAM (0xA000-0xFDFF or 0xFF80-0xFFFE) so the assistant does not continuously overwrite ROM mapper or hardware-control registers. Use 'poke' for an intentional one-time raw write.";
            return safe;
        }
        if (ActiveNesCore is not null)
        {
            bool safe = address <= 0x1FFF || (address >= 0x6000 && address <= 0x7FFF);
            reason = safe ? string.Empty : "For NES, freeze cheats are restricted to internal RAM/mirrors (0x0000-0x1FFF) and cartridge RAM (0x6000-0x7FFF), avoiding continuous writes to PPU/APU/mapper registers. Use 'poke' for an intentional one-time raw write.";
            return safe;
        }
        reason = "Load a cartridge before adding a cheat.";
        return false;
    }

    private void ApplyAssistantCheats()
    {
        if (_assistantCheats.Count == 0) return;
        foreach (MemoryCheat cheat in _assistantCheats)
            TryWriteActiveMemory(cheat.Address, cheat.Value, out _);
    }

    private void ResetAssistantCheatsForCartridgeChange() => _assistantCheats.Clear();

    private void AssistantOnCartridgeLoaded()
    {
        string file = _emulationHost.LoadedCartridge is null ? "cartridge" : Path.GetFileName(_emulationHost.LoadedCartridge.FilePath);
        AppendAssistant("LEESIR", $"Loaded {file}. {BuildAssistantStatus()} Previous RAM freeze cheats were cleared for safety.");
    }

    private void AssistantOnCartridgeUnloaded() => AppendAssistant("LEESIR", "Cartridge unloaded. Active RAM freeze cheats were cleared.");

    private void AppendAssistant(string speaker, string text)
    {
        if (_assistantTranscript is null) return;
        _assistantTranscript.SelectionStart = _assistantTranscript.TextLength;
        _assistantTranscript.SelectionColor = speaker == "You" ? Color.FromArgb(32, 89, 160) : Color.FromArgb(32, 32, 32);
        _assistantTranscript.SelectionFont = new Font(_assistantTranscript.Font, FontStyle.Bold);
        _assistantTranscript.AppendText(speaker + ": ");
        _assistantTranscript.SelectionColor = Color.FromArgb(32, 32, 32);
        _assistantTranscript.SelectionFont = new Font(_assistantTranscript.Font, FontStyle.Regular);
        _assistantTranscript.AppendText(text + Environment.NewLine + Environment.NewLine);
        _assistantTranscript.SelectionStart = _assistantTranscript.TextLength;
        _assistantTranscript.ScrollToCaret();
    }
}
