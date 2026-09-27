using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Setup
{
    /// <summary>The Setup Wizard's steps, in execution order.</summary>
    public enum SetupStepId
    {
        ConfigureProject = 0,
        GenerateData = 1,
        DownloadAvatars = 2,
        BuildCharacters = 3,
        BuildArena = 4,
        Play = 5,
    }

    /// <summary>Visual state of a step in the wizard.</summary>
    public enum SetupStepState
    {
        /// <summary>Not done yet (something is missing).</summary>
        Todo = 0,
        /// <summary>Everything this step produces is present.</summary>
        Done,
        /// <summary>Usable, but something deserves attention.</summary>
        Warning,
        /// <summary>Needs an earlier step first.</summary>
        Blocked,
        /// <summary>Executing right now.</summary>
        Running,
        /// <summary>The last run failed.</summary>
        Failed,
    }

    /// <summary>What the wizard shows for one step: state, one-line summary and the list of missing things.</summary>
    public sealed class SetupStepStatus
    {
        public SetupStepState State = SetupStepState.Todo;
        public string Summary = string.Empty;
        public readonly List<string> Missing = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Derives Todo / Warning / Done from the collected lists (unless a state was forced).</summary>
        public SetupStepStatus Resolve(string doneSummary, string todoSummary)
        {
            if (Missing.Count > 0) { State = SetupStepState.Todo; Summary = todoSummary; }
            else if (Warnings.Count > 0) { State = SetupStepState.Warning; Summary = doneSummary; }
            else { State = SetupStepState.Done; Summary = doneSummary; }
            return this;
        }
    }

    /// <summary>Result of executing one step.</summary>
    public readonly struct SetupStepResult
    {
        public readonly bool Success;
        public readonly bool Cancelled;
        public readonly string Message;

        private SetupStepResult(bool success, bool cancelled, string message)
        {
            Success = success;
            Cancelled = cancelled;
            Message = message ?? string.Empty;
        }

        public static SetupStepResult Ok(string message) => new SetupStepResult(true, false, message);
        public static SetupStepResult Fail(string message) => new SetupStepResult(false, false, message);
        public static SetupStepResult Cancel(string message) => new SetupStepResult(false, true, message);
    }

    /// <summary>Per-project editor preferences of the wizard (EditorPrefs are machine-wide, so keys include the project path).</summary>
    public static class SetupPrefs
    {
        /// <summary>A key unique to this project.</summary>
        public static string Key(string name) => "DodgeballUltra:" + Application.dataPath + ":" + name;

        public static bool GetBool(string name, bool fallback) => EditorPrefs.GetBool(Key(name), fallback);
        public static void SetBool(string name, bool value) => EditorPrefs.SetBool(Key(name), value);
        public static int GetInt(string name, int fallback) => EditorPrefs.GetInt(Key(name), fallback);
        public static void SetInt(string name, int value) => EditorPrefs.SetInt(Key(name), value);
        public static string GetString(string name, string fallback) => EditorPrefs.GetString(Key(name), fallback);
        public static void SetString(string name, string value) => EditorPrefs.SetString(Key(name), value);
    }
}
