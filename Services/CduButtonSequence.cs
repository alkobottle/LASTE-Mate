using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LASTE_Mate.Models;
using LASTE_Mate.Services;
using NLog;
using LASTE_Mate.ViewModels;

namespace LASTE_Mate.Services;

/// <summary>
/// Represents a single button command in a sequence.
/// </summary>
public class CduButtonCommand
{
    public string Control { get; set; } = string.Empty;
    public int Value { get; set; }
    public int DelayAfterMs { get; set; }
    public string? Description { get; set; }

    public CduButtonCommand(string control, int value, int delayAfterMs = 50, string? description = null)
    {
        Control = control;
        Value = value;
        DelayAfterMs = delayAfterMs;
        Description = description;
    }
}

/// <summary>
/// Calculates and executes CDU button sequences for entering wind correction data.
/// </summary>
public class CduButtonSequence
{
    private static readonly ILogger Logger = LoggingService.GetLogger<CduButtonSequence>();
    private readonly DcsBiosService _biosService;

    public CduButtonSequence(DcsBiosService biosService)
    {
        _biosService = biosService ?? throw new ArgumentNullException(nameof(biosService));
    }

    /// <summary>
    /// Generates the button sequence for page & height setup (based on user's worked-out sequence).
    /// This is the initial setup before entering wind and temperature data.
    /// </summary>
    public List<CduButtonCommand> GeneratePageAndHeightSetup()
    {
        return new List<CduButtonCommand>
        {
            new("CDU_SYS", 1, 50, "Press SYS"),
            new("CDU_SYS", 0, 50, "Release SYS"),
            new("CDU_LSK_3R", 1, 50, "Press LSK 3R (LASTE)"),
            new("CDU_LSK_3R", 0, 50, "Release LSK 3R"),
            new("CDU_LSK_9R", 1, 50, "Press LSK 9R (WIND)"),
            new("CDU_LSK_9R", 0, 50, "Release LSK 9R"),
            new("CDU_LSK_7R", 1, 50, "Press LSK 7R (CLR)"),
            new("CDU_LSK_7R", 0, 50, "Release LSK 7R"),
            new("CDU_LSK_7R", 1, 50, "Press LSK 7R (Confirm CLR)"),
            new("CDU_LSK_7R", 0, 50, "Release LSK 7R"),
            new("CDU_0", 1, 50, "Press 0 (first digit)"),
            new("CDU_0", 0, 50, "Release 0"),
            new("CDU_0", 1, 50, "Press 0 (second digit)"),
            new("CDU_0", 0, 50, "Release 0"),
            new("CDU_LSK_5L", 1, 50, "Press LSK 5L (select altitude)"),
            new("CDU_LSK_5L", 0, 50, "Release LSK 5L"),
            new("CDU_0", 1, 50, "Press 0 (first digit)"),
            new("CDU_0", 0, 50, "Release 0"),
            new("CDU_1", 1, 50, "Press 1 (second digit)"),
            new("CDU_1", 0, 50, "Release 1"),
            new("CDU_LSK_7L", 1, 50, "Press LSK 7L (select altitude)"),
            new("CDU_LSK_7L", 0, 50, "Release LSK 7L"),
            new("CDU_0", 1, 50, "Press 0 (first digit)"),
            new("CDU_0", 0, 50, "Release 0"),
            new("CDU_2", 1, 50, "Press 2 (second digit)"),
            new("CDU_2", 0, 50, "Release 2"),
            new("CDU_LSK_9L", 1, 50, "Press LSK 9L (select altitude)"),
            new("CDU_LSK_9L", 0, 50, "Release LSK 9L"),
            new("CDU_PG", 0, 50, "Page down (PAGE rocker to 2)"),
            new("CDU_PG", 1, 50, "Page rocker return to center"),
            new("CDU_0", 1, 50, "Press 0 (first digit)"),
            new("CDU_0", 0, 50, "Release 0"),
            new("CDU_7", 1, 50, "Press 7 (second digit)"),
            new("CDU_7", 0, 50, "Release 7"),
            new("CDU_LSK_5L", 1, 50, "Press LSK 5L (select altitude)"),
            new("CDU_LSK_5L", 0, 50, "Release LSK 5L"),
            new("CDU_2", 1, 50, "Press 2 (first digit)"),
            new("CDU_2", 0, 50, "Release 2"),
            new("CDU_6", 1, 50, "Press 6 (second digit)"),
            new("CDU_6", 0, 50, "Release 6"),
            new("CDU_LSK_7L", 1, 50, "Press LSK 7L (select altitude)"),
            new("CDU_LSK_7L", 0, 50, "Release LSK 7L"),
            new("CDU_PG", 2, 300, "Page up (PAGE rocker to 0)"),
            new("CDU_PG", 1, 50, "Page rocker return to center"),
        };
    }

    /// <summary>
    /// Executes a sequence of button commands with delays and error checking.
    /// </summary>
    public async Task<bool> ExecuteSequenceAsync(List<CduButtonCommand> sequence, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sequence == null || sequence.Count == 0)
        {
            return false;
        }

        // Track currently pressed button to ensure it's released on cancellation
        string? currentlyPressedButton = null;
        bool isPageRockerActive = false;

        try
        {
            foreach (var cmd in sequence)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(cmd.Description ?? $"Executing {cmd.Control} {cmd.Value}");

                bool success;
                
                // Handle PAGE rocker specially
                if (cmd.Control == "CDU_PG" && (cmd.Value == 0 || cmd.Value == 2))
                {
                    // SetPageRockerAsync is atomic: sets to target, holds, then returns to center
                    isPageRockerActive = true;
                    currentlyPressedButton = cmd.Control;
                    success = await _biosService.SetPageRockerAsync(cmd.Value, cmd.DelayAfterMs);
                    // After SetPageRockerAsync completes, rocker is back at center
                    isPageRockerActive = false;
                    currentlyPressedButton = null;
                }
                else if (cmd.Control == "CDU_PG" && cmd.Value == 1)
                {
                    // Regular command to return to center (in case it wasn't handled by SetPageRockerAsync)
                    success = await _biosService.SendControlAsync(cmd.Control, cmd.Value);
                    isPageRockerActive = false;
                    currentlyPressedButton = null;
                }
                else
                {
                    // Track press/release state for regular buttons
                    if (cmd.Value == 1)
                    {
                        // Press command
                        currentlyPressedButton = cmd.Control;
                    }
                    else if (cmd.Value == 0)
                    {
                        // Release command
                        currentlyPressedButton = null;
                    }

                    success = await _biosService.SendControlAsync(cmd.Control, cmd.Value);
                }

                if (!success)
                {
                    Logger.Warn("Failed to execute command: {Control} {Value}", cmd.Control, cmd.Value);
                    return false;
                }

                if (cmd.DelayAfterMs > 0)
                {
                    await Task.Delay(cmd.DelayAfterMs, cancellationToken);
                }

                if (ShouldCheckForError(cmd))
                {
                    await Task.Delay(100, cancellationToken);

                    if (await CheckAndRecoverFromErrorAsync(cancellationToken))
                    {
                        Logger.Info("Retrying command after error recovery: {Control} {Value}", cmd.Control, cmd.Value);
                        
                        if (cmd.Control == "CDU_PG" && (cmd.Value == 0 || cmd.Value == 2))
                        {
                            isPageRockerActive = true;
                            currentlyPressedButton = cmd.Control;
                            success = await _biosService.SetPageRockerAsync(cmd.Value, cmd.DelayAfterMs);
                            isPageRockerActive = false;
                            currentlyPressedButton = null;
                        }
                        else if (cmd.Control == "CDU_PG" && cmd.Value == 1)
                        {
                            success = await _biosService.SendControlAsync(cmd.Control, cmd.Value);
                            isPageRockerActive = false;
                            currentlyPressedButton = null;
                        }
                        else
                        {
                            if (cmd.Value == 1)
                            {
                                currentlyPressedButton = cmd.Control;
                            }
                            else if (cmd.Value == 0)
                            {
                                currentlyPressedButton = null;
                            }
                            success = await _biosService.SendControlAsync(cmd.Control, cmd.Value);
                        }

                        if (!success)
                        {
                            return false;
                        }

                        if (cmd.DelayAfterMs > 0)
                        {
                            await Task.Delay(cmd.DelayAfterMs, cancellationToken);
                        }

                        if (await CheckAndRecoverFromErrorAsync(cancellationToken))
                        {
                            Logger.Error("Error persists after retry for: {Control} {Value}", cmd.Control, cmd.Value);
                        }
                    }
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrEmpty(currentlyPressedButton))
            {
                Logger.Info("Cancellation detected, releasing pressed button: {Button}", currentlyPressedButton);
                
                try
                {
                    if (isPageRockerActive && currentlyPressedButton == "CDU_PG")
                    {
                        await _biosService.SendControlAsync("CDU_PG", 1);
                    }
                    else
                    {
                        await _biosService.SendControlAsync(currentlyPressedButton, 0);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error releasing button on cancellation");
                }
            }
            
            throw;
        }
    }

    private static bool ShouldCheckForError(CduButtonCommand cmd)
    {
        return cmd.Control.StartsWith("CDU_LSK_") || 
               cmd.Control.StartsWith("CDU_") && char.IsDigit(cmd.Control[4]) ||
               cmd.Control == "CDU_PG";
    }

    private async Task<bool> CheckAndRecoverFromErrorAsync(CancellationToken cancellationToken)
    {
        if (!_biosService.HasCduError())
        {
            return false;
        }

        Logger.Warn("CDU error detected, attempting recovery...");
        
        var cleared = await _biosService.ClearCduErrorAsync();
        if (!cleared)
        {
            Logger.Error("Failed to clear CDU error");
            return true;
        }

        await Task.Delay(200, cancellationToken);

        if (_biosService.HasCduError())
        {
            Logger.Warn("CDU error still present after CLR");
            return true;
        }

        Logger.Info("CDU error cleared successfully");
        return true;
    }

    private List<CduButtonCommand> GenerateWindDataEntry(CduWindLineViewModel[] windLines)
    {
        var sequence = new List<CduButtonCommand>();

        sequence.Add(new("CDU_LSK_5R", 1, 50, "Press LSK 5R (WNDEDIT)"));
        sequence.Add(new("CDU_LSK_5R", 0, 50, "Release LSK 5R"));

        var wind00 = windLines.FirstOrDefault(w => w.AltKft == 0);
        var wind01 = windLines.FirstOrDefault(w => w.AltKft == 1);
        var wind02 = windLines.FirstOrDefault(w => w.AltKft == 2);
        var wind07 = windLines.FirstOrDefault(w => w.AltKft == 7);
        var wind26 = windLines.FirstOrDefault(w => w.AltKft == 26);

        Enter5DigitWindData(sequence, wind00, "CDU_LSK_5L", "altitude 00");
        Enter5DigitWindData(sequence, wind01, "CDU_LSK_7L", "altitude 01");
        Enter5DigitWindData(sequence, wind02, "CDU_LSK_9L", "altitude 02");

        sequence.Add(new("CDU_PG", 0, 50, "Page down (PAGE rocker to 0)"));
        sequence.Add(new("CDU_PG", 1, 50, "Page rocker return to center"));

        Enter5DigitWindData(sequence, wind07, "CDU_LSK_3L", "altitude 07");
        Enter5DigitWindData(sequence, wind26, "CDU_LSK_5L", "altitude 26");

        sequence.Add(new("CDU_PG", 2, 300, "Page up (PAGE rocker to 2)"));
        sequence.Add(new("CDU_PG", 1, 50, "Page rocker return to center"));

        return sequence;
    }

    private List<CduButtonCommand> GenerateTemperatureDataEntry(CduWindLineViewModel[] windLines)
    {
        var sequence = new List<CduButtonCommand>();

        var wind00 = windLines.FirstOrDefault(w => w.AltKft == 0);
        var wind01 = windLines.FirstOrDefault(w => w.AltKft == 1);
        var wind02 = windLines.FirstOrDefault(w => w.AltKft == 2);
        var wind07 = windLines.FirstOrDefault(w => w.AltKft == 7);
        var wind26 = windLines.FirstOrDefault(w => w.AltKft == 26);

        Enter2DigitTemperatureData(sequence, wind00, "CDU_LSK_5R", "altitude 00");
        Enter2DigitTemperatureData(sequence, wind01, "CDU_LSK_7R", "altitude 01");
        Enter2DigitTemperatureData(sequence, wind02, "CDU_LSK_9R", "altitude 02");

        sequence.Add(new("CDU_PG", 0, 50, "Page down (PAGE rocker to 0)"));
        sequence.Add(new("CDU_PG", 1, 50, "Page rocker return to center"));

        Enter2DigitTemperatureData(sequence, wind07, "CDU_LSK_3R", "altitude 07");
        Enter2DigitTemperatureData(sequence, wind26, "CDU_LSK_5R", "altitude 26");

        sequence.Add(new("CDU_PG", 2, 300, "Page up (PAGE rocker to 2)"));
        sequence.Add(new("CDU_PG", 1, 50, "Page rocker return to center"));

        return sequence;
    }

    private void Enter5DigitWindData(List<CduButtonCommand> sequence, CduWindLineViewModel? windLine, string lskButton, string description)
    {
        if (windLine == null)
        {
            Logger.Warn("No wind data for {Description}, skipping", description);
            return;
        }

        var brgPlusSpd = windLine.BrgPlusSpd ?? "00000";
        
        if (brgPlusSpd.Length != 5)
        {
            brgPlusSpd = brgPlusSpd.PadLeft(5, '0').Substring(0, 5);
        }

        for (int i = 0; i < 5; i++)
        {
            var digit = brgPlusSpd[i];
            var control = $"CDU_{digit}";
            sequence.Add(new(control, 1, 50, $"Press {digit} ({description}, digit {i + 1})"));
            sequence.Add(new(control, 0, 50, $"Release {digit}"));
        }

        sequence.Add(new(lskButton, 1, 50, $"Press {lskButton} (select {description})"));
        sequence.Add(new(lskButton, 0, 50, $"Release {lskButton}"));
    }

    private void Enter2DigitTemperatureData(List<CduButtonCommand> sequence, CduWindLineViewModel? windLine, string lskButton, string description)
    {
        if (windLine == null)
        {
            Logger.Warn("No temperature data for {Description}, skipping", description);
            return;
        }

        var temp = windLine.TmpC;
        var isNegative = temp < 0;
        var absTemp = Math.Abs(temp);

        var tempStr = absTemp.ToString("D2");
        if (tempStr.Length > 2)
        {
            tempStr = tempStr.Substring(tempStr.Length - 2);
        }

        for (int i = 0; i < 2; i++)
        {
            var digit = tempStr[i];
            var control = $"CDU_{digit}";
            sequence.Add(new(control, 1, 50, $"Press {digit} (temp {description}, digit {i + 1})"));
            sequence.Add(new(control, 0, 50, $"Release {digit}"));
        }

        int pressCount = isNegative ? 2 : 1;
        for (int i = 0; i < pressCount; i++)
        {
            sequence.Add(new(lskButton, 1, 50, $"Press {lskButton} (select {description}, press {i + 1}/{pressCount})"));
            sequence.Add(new(lskButton, 0, 50, $"Release {lskButton}"));
        }
    }

    public List<CduButtonCommand> GenerateCompleteSequence(CduWindLineViewModel[] windLines)
    {
        var sequence = new List<CduButtonCommand>();

        sequence.AddRange(GeneratePageAndHeightSetup());
        sequence.AddRange(GenerateWindDataEntry(windLines));
        sequence.AddRange(GenerateTemperatureDataEntry(windLines));

        return sequence;
    }
}

