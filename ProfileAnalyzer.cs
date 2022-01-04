﻿// Copyright 2020 Google LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Microsoft.Windows.EventTracing.Cpu;
using System.Text;

namespace IdleWakeups
{
  internal class ProfileAnalyzer
  {
    public struct Options
    {
      public string EtlFileName { get; set; }
      public decimal TimeStart { get; set; }
      public decimal TimeEnd { get; set; }
      public HashSet<string> ProcessFilterSet { get; set; }
      public bool Tabbed { get; set; }
    }

    private struct IdleWakeup
    {
      public long ContextSwitchCount { get; set; }
      public int ProcessId { get; set; }
      public ChromeProcessType ProcessType { get; set; }
      public string ThreadName { get; set; }
    }

    private struct ChromeProcessType
    {
      public ChromeProcessType(string type, string? subType)
      {
        Type = type;
        SubType = subType;
      }
      public string Type { get; set; }
      public string? SubType { get; set; }
    }

    public ProfileAnalyzer(Options options)
    {
      _options = options;
    }

    private ChromeProcessType GetChromeProcessType(string commandLine)
    {
      if (string.IsNullOrEmpty(commandLine))
      {
        return new ChromeProcessType("", "");
      }

      const string kProcessTypeParam = "--type=";
      const string kRendererProcessType = "renderer";
      const string kExtensionProcess = "extension";
      const string kExtensionParam = "--extension-process";
      const string kUtilityProcessType = "utility";
      const string kUtilitySubTypeParam = "--utility-sub-type=";
      const string kBrowserProcessType = "browser";
      const string kChrashpadProcess = "crashpad-handler";
      const string kChrashpadProcessShort = "crashpad";

      string type = kBrowserProcessType;
      string? subtype = null;

      var commandLineSplit = commandLine.Split();
      foreach (var commandLineArg in commandLineSplit)
      {
        if (commandLineArg.StartsWith(kProcessTypeParam))
        {
          type = commandLineArg[kProcessTypeParam.Length..];
          if (type == kChrashpadProcess)
          {
            type = kChrashpadProcessShort;
          }
          else if (type == kRendererProcessType && commandLine.Contains(kExtensionParam))
          {
            type = kExtensionProcess;
          }
          else if (type == kUtilityProcessType)
          {
            var utilitySubType = commandLineSplit.First(s => s.StartsWith(kUtilitySubTypeParam));
            if (utilitySubType != null)
            {
              // Example: 'video_capture.mojom.VideoCaptureService'.
              subtype = utilitySubType[kUtilitySubTypeParam.Length..];
              // Return only the last 'VideoCaptureService' part of this string.
              var subs = subtype.Split('.');
              subtype = subs[subs.Length - 1];
              break;
            }
          }
        }
      }

      return new ChromeProcessType(type, subtype);
    }

    public void AddSample(ICpuThreadActivity sample)
    {
      var contextSwitch = sample.SwitchIn.ContextSwitch;

      var timestamp = contextSwitch.Timestamp.RelativeTimestamp.TotalSeconds;
      if (timestamp < _options.TimeStart || timestamp > _options.TimeEnd)
      {
        return;
      }

      var switchInImageName = contextSwitch.SwitchIn.Process.ImageName;
      var switchOutImageName = contextSwitch.SwitchOut.Process.ImageName;

      // Check if all processes shall be analyzed when switched in (filter set to '*') or if a
      // filter has been set (e.g. 'chrome.exe') and it contains the process name switching in.
      if (_options.ProcessFilterSet == null ||
          _options.ProcessFilterSet.Contains(switchInImageName))
      {
        _wallTimeStart = Math.Min(_wallTimeStart, timestamp);
        _wallTimeEnd = Math.Max(_wallTimeEnd, timestamp);

        _filteredProcessContextSwitch++;
        if (switchOutImageName == "Idle")
        {
          _filteredProcessIdleContextSwitch++;

          var switchInThreadId = contextSwitch.SwitchIn.ThreadId;

          IdleWakeup iwakeup;
          if (!_idleWakeupsByThreadId.TryGetValue(switchInThreadId, out iwakeup))
          {
            // A new thread ID was found: update the context-switch counter and add the rest of the
            // information about the detected idle wakeup. These values are only stored once.
            iwakeup.ContextSwitchCount++;
            iwakeup.ProcessId = contextSwitch.SwitchIn.Process.Id;
            iwakeup.ThreadName = contextSwitch.SwitchIn.Thread.Name;

            var commandLine = contextSwitch.SwitchIn.Process.CommandLine;
            var processType = GetChromeProcessType(commandLine);
            iwakeup.ProcessType = processType;

          }
          else
          {
            // Thread ID already exists: only update the context-switch counter for this key.
            iwakeup.ContextSwitchCount++;
          }

          _idleWakeupsByThreadId[switchInThreadId] = iwakeup;
        }

        // Get the process that made this thread become eligible to be switched in, if available.
        var readyingProcessImageName = sample.ReadyingProcess?.ImageName ?? "Unknown";
        if (sample.ReadyThreadEvent?.IsExecutingDeferredProcedureCall ?? false)
        {
          // If the readying thread is executing a deferred procedure call then the process is not
          // relevant; it's just a temporary host for the DPC.
          readyingProcessImageName = "DPC";
        }
        _filteredProcessReadyProcesses.TryGetValue(readyingProcessImageName, out long count);
        _filteredProcessReadyProcesses[readyingProcessImageName] = count + 1;
      }
    }

    public void WriteSummary()
    {
      WriteHeader("High level summary:");

      var sep = _options.Tabbed ? "\t" : " : ";
      if (_wallTimeStart < _wallTimeEnd)
      {
        var durationMs = (_wallTimeEnd - _wallTimeStart) * 1000;
        Console.WriteLine("{0,-25}{1}{2:F}", "Duration (msec)", sep, durationMs);
      }
      Console.Write("{0,-25}{1}", "Process filter", sep);
      Console.WriteLine(ProcessFilterToString());
      Console.WriteLine("{0,-25}{1}{2}", "Context switches (On-CPU)", sep, _filteredProcessContextSwitch);
      Console.WriteLine("{0,-25}{1}{2}", "Idle wakeups", sep, _filteredProcessIdleContextSwitch);
      Console.WriteLine("{0,-25}{1}{2:F}", "Idle wakeups (%)",
        sep, 100 * (double)_filteredProcessIdleContextSwitch / _filteredProcessContextSwitch);
      Console.WriteLine();

      WriteHeader("Readying processes are:");

      var composite = "{0,-25}{1}{2,6}";
      var sortedFilteredProcessReadyProcesses =
        new List<KeyValuePair<string, long>>(_filteredProcessReadyProcesses);
      sortedFilteredProcessReadyProcesses.Sort((x, y) => y.Value.CompareTo(x.Value));
      foreach (var filteredProcessReadyProcess in sortedFilteredProcessReadyProcesses)
      {
        int length = filteredProcessReadyProcess.Key.Length;
        Console.WriteLine(composite,
          filteredProcessReadyProcess.Key[..Math.Min(length, 25)], sep,
          filteredProcessReadyProcess.Value);
      }
      var totalReadyProcesses = _filteredProcessReadyProcesses.Sum(x => x.Value);
      Console.WriteLine(composite, "", sep, totalReadyProcesses);
      Console.WriteLine();

      var processFilter = ProcessFilterToString();
      WriteHeader($"Idle-wakeup (Idle -> {processFilter}) distribution with thread IDs as keys:");

      composite = "{0,6}{1}{2,6}{3}{4,-12}{5}{6,-20}{7}{8,-55}{9}{10,6}";
      sep = _options.Tabbed ? "\t" : " ";
      string header = string.Format(composite,
          "TID", sep,
          "PID", sep,
          "Type", sep,
          "Subtype", sep,
          "Thread Name", sep,
          "Count");
      Console.WriteLine(header);
      StringBuilder sbHeaderLine = new StringBuilder();
      if (!_options.Tabbed)
      {
        for (int i = 0; i < header.Length + 1; i++)
        {
          sbHeaderLine.Append("=");
        }
        Console.WriteLine(sbHeaderLine.ToString());
      }

      composite = "{0,6}{1}{2,6}{3}{4,-12}{5}{6,-20}{7}{8,-55}{9}{10,6}";
      var sortedIdleWakeupsByThreadId = new List<KeyValuePair<int, IdleWakeup>>(_idleWakeupsByThreadId);
      sortedIdleWakeupsByThreadId.Sort((x, y)
          => y.Value.ContextSwitchCount.CompareTo(x.Value.ContextSwitchCount));
      foreach (var idleWakeup in sortedIdleWakeupsByThreadId.Take(30))
      {
        Console.WriteLine(composite,
          idleWakeup.Key, sep,
          idleWakeup.Value.ProcessId, sep,
          idleWakeup.Value.ProcessType.Type, sep,
          idleWakeup.Value.ProcessType.SubType, sep,
          idleWakeup.Value.ThreadName, sep,
          idleWakeup.Value.ContextSwitchCount);
      }

      var totalContextSwitchCount = _idleWakeupsByThreadId.Sum(x => x.Value.ContextSwitchCount);
      if (!_options.Tabbed)
      {
        Console.WriteLine(sbHeaderLine.ToString());
      }
      Console.WriteLine(composite,
          "", sep,
          "", sep,
          "", sep,
          "", sep,
          "", sep,
          totalContextSwitchCount);
    }

    private void WriteHeader(string header)
    {
      Console.ForegroundColor = ConsoleColor.Yellow;
      Console.WriteLine(header);
      Console.WriteLine();
      Console.ForegroundColor = ConsoleColor.White;
    }

    private string ProcessFilterToString()
    {
      StringBuilder sb = new StringBuilder();
      if (_options.ProcessFilterSet == null)
        return "*";
      foreach (var item in _options.ProcessFilterSet)
      {
        sb.Append(item);
        sb.Append(" ");
      }
      return sb.ToString().TrimEnd();
    }

    private readonly Options _options;

    private long _filteredProcessContextSwitch;

    private long _filteredProcessIdleContextSwitch;

    private decimal _wallTimeStart = decimal.MaxValue;
    private decimal _wallTimeEnd = 0;

    private Dictionary<int, IdleWakeup> _idleWakeupsByThreadId = new();

    private Dictionary<string, long> _filteredProcessReadyProcesses = new();
  }
}
