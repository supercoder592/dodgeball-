using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Non-blocking downloader of Microsoft Rocketbox files (avatars, textures, portraits, motion-capture clips) from the
    /// manifest's pinned raw URL. Driven by <see cref="EditorApplication.update"/>: up to
    /// <see cref="CharacterPipelineSettings.maxConcurrentDownloads"/> <see cref="UnityWebRequest"/>s run in parallel, each
    /// file is streamed to <c>Temp/</c> (outside Assets/, so Unity never imports half-written files) and moved into place
    /// once complete. Network errors, HTTP 5xx / 429 are retried with exponential backoff; files already on disk are
    /// skipped. On completion the MIT license file is written and the AssetDatabase refreshed.
    /// <para>Mirrors <c>Tools/fetch_rocketbox.py</c> (same file plan, same license text).</para>
    /// </summary>
    internal sealed class RocketboxDownloader
    {
        private const string UserAgent = "DodgeballUltra-UnityEditor/1.0 (+https://github.com/microsoft/Microsoft-Rocketbox)";
        private const double ProgressInterval = 0.1;

        /// <summary>One file to fetch.</summary>
        public sealed class Item
        {
            /// <summary>Repository-relative source path (for messages).</summary>
            public string Source;
            /// <summary>Full download URL.</summary>
            public string Url;
            /// <summary>Project-relative destination (Assets/ThirdParty/Rocketbox/...).</summary>
            public string AssetPath;
            /// <summary>Attempts made so far.</summary>
            public int Attempts;
            /// <summary>Editor time before which a retry must not start.</summary>
            public double NotBefore;
            /// <summary>Last error (for the report).</summary>
            public string LastError;
        }

        private sealed class Active
        {
            public Item Item;
            public UnityWebRequest Request;
            public string PartPath;
        }

        private static RocketboxDownloader s_current;

        private readonly Queue<Item> _pending = new Queue<Item>();
        private readonly List<Item> _waitingRetry = new List<Item>();
        private readonly List<Active> _active = new List<Active>();
        private readonly List<string> _failures = new List<string>();
        private readonly Action<float, string> _onProgress;
        private readonly Action<bool, string> _onComplete;
        private readonly string _commit;
        private readonly string _tempFolder;
        private readonly int _total;
        private readonly int _concurrency;
        private readonly int _maxAttempts;
        private readonly float _retryBaseDelay;
        private readonly int _timeout;
        private readonly List<string> _preflightProblems;

        private int _done;
        private int _downloaded;
        private int _skipped;
        private long _bytes;
        private double _lastProgressTime;
        private bool _cancelRequested;
        private bool _finished;
        private string _lastFile;

        /// <summary>True while a download is running.</summary>
        public static bool IsRunning => s_current != null && !s_current._finished;

        /// <summary>Requests cancellation of the running download (completes with success = false on the next tick).</summary>
        public static void Cancel()
        {
            if (!IsRunning) return;
            s_current._cancelRequested = true;
            // Complete right away as well: callers expect the cancellation to be reported even if the editor is idle.
            s_current.Tick();
        }

        /// <summary>
        /// Starts downloading <paramref name="items"/>. <paramref name="preflightProblems"/> (e.g. avatars missing from the
        /// manifest) are reported as failures at the end. Returns false (and reports through
        /// <paramref name="onComplete"/>) when a download is already running.
        /// </summary>
        public static bool Start(string commit, List<Item> items, List<string> preflightProblems, Action<float, string> onProgress,
            Action<bool, string> onComplete)
        {
            if (IsRunning)
            {
                onComplete?.Invoke(false, "A Rocketbox download is already running.");
                return false;
            }

            var downloader = new RocketboxDownloader(commit, items, preflightProblems, onProgress, onComplete);
            s_current = downloader;
            EditorApplication.update += downloader.Tick;
            AssemblyReloadEvents.beforeAssemblyReload += downloader.OnBeforeAssemblyReload;
            downloader.Report(true);
            return true;
        }

        private RocketboxDownloader(string commit, List<Item> items, List<string> preflightProblems, Action<float, string> onProgress,
            Action<bool, string> onComplete)
        {
            CharacterPipelineSettings settings = CharacterPipelineSettings.Instance;
            _commit = string.IsNullOrEmpty(commit) ? "unknown" : commit;
            _onProgress = onProgress;
            _onComplete = onComplete;
            _concurrency = Mathf.Clamp(settings.maxConcurrentDownloads, 1, 8);
            _maxAttempts = Mathf.Max(1, settings.maxDownloadAttempts);
            _retryBaseDelay = Mathf.Max(0.1f, settings.retryBaseDelaySeconds);
            _timeout = Mathf.Max(10, settings.requestTimeoutSeconds);
            _preflightProblems = preflightProblems ?? new List<string>();
            _tempFolder = Path.Combine(RocketboxAssetSet.ProjectRoot, "Temp", "DodgeballRocketbox");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (items != null)
            {
                foreach (Item item in items)
                    if (item != null && !string.IsNullOrEmpty(item.AssetPath) && seen.Add(item.AssetPath)) _pending.Enqueue(item);
            }
            _total = _pending.Count;
            CleanTempFolder();
        }

        // ================================================================================================= update loop

        private void Tick()
        {
            if (_finished) return;
            try
            {
                if (_cancelRequested)
                {
                    AbortAll();
                    Finish(false, $"Download cancelled ({_done}/{_total} files done). Already downloaded files are kept.", false);
                    return;
                }

                PollActive();
                StartRetries();
                FillSlots();
                Report(false);

                if (_pending.Count == 0 && _active.Count == 0 && _waitingRetry.Count == 0) Complete();
            }
            catch (Exception e)
            {
                AbortAll();
                Finish(false, "Rocketbox download failed: " + e.Message, true);
            }
        }

        private void FillSlots()
        {
            while (_active.Count < _concurrency && _pending.Count > 0)
            {
                Item item = _pending.Dequeue();
                if (RocketboxAssetSet.FileExistsNonEmpty(item.AssetPath))
                {
                    _skipped++;
                    _done++;
                    continue;
                }
                Begin(item);
            }
        }

        private void StartRetries()
        {
            if (_waitingRetry.Count == 0) return;
            double now = EditorApplication.timeSinceStartup;
            for (int i = _waitingRetry.Count - 1; i >= 0 && _active.Count < _concurrency; i--)
            {
                Item item = _waitingRetry[i];
                if (item.NotBefore > now) continue;
                _waitingRetry.RemoveAt(i);
                Begin(item);
            }
        }

        private void Begin(Item item)
        {
            item.Attempts++;
            Directory.CreateDirectory(_tempFolder);
            string part = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + ".part");
            var request = new UnityWebRequest(item.Url, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DownloadHandlerFile(part) { removeFileOnAbort = true },
                timeout = _timeout,
                redirectLimit = 8,
            };
            try
            {
                request.SetRequestHeader("User-Agent", UserAgent);
            }
            catch (Exception)
            {
                // Some platforms forbid overriding the user agent; the default one works too.
            }
            request.SendWebRequest();
            _active.Add(new Active { Item = item, Request = request, PartPath = part });
            _lastFile = Path.GetFileName(item.AssetPath);
        }

        private void PollActive()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Active a = _active[i];
                if (!a.Request.isDone) continue;
                _active.RemoveAt(i);

                UnityWebRequest request = a.Request;
                long code = request.responseCode;
                UnityWebRequest.Result result = request.result;
                bool ok = result == UnityWebRequest.Result.Success && code >= 200 && code < 300;
                string error = ok ? null : DescribeError(request);
                request.Dispose(); // closes the part file

                if (ok)
                {
                    long size = SafeLength(a.PartPath);
                    if (size <= 0)
                    {
                        ok = false;
                        error = "empty response";
                    }
                    else
                    {
                        try
                        {
                            MoveIntoPlace(a.PartPath, a.Item.AssetPath);
                            _bytes += size;
                            _downloaded++;
                            _done++;
                            continue;
                        }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                        {
                            ok = false;
                            error = "could not write the file: " + e.Message;
                            code = 0;
                        }
                    }
                }

                TryDelete(a.PartPath);
                a.Item.LastError = error;
                if (IsRetryable(result, code) && a.Item.Attempts < _maxAttempts)
                {
                    // Exponential backoff with jitter: base, 2x base, 4x base ... (capped at 30 s).
                    double delay = Math.Min(30.0, _retryBaseDelay * Math.Pow(2, a.Item.Attempts - 1)) *
                                   (0.75 + UnityEngine.Random.value * 0.5);
                    a.Item.NotBefore = EditorApplication.timeSinceStartup + delay;
                    _waitingRetry.Add(a.Item);
                }
                else
                {
                    _failures.Add($"{a.Item.Source}: {error} ({a.Item.Url})");
                    _done++;
                }
            }
        }

        private static bool IsRetryable(UnityWebRequest.Result result, long code)
        {
            if (result == UnityWebRequest.Result.ConnectionError) return true;
            if (code == 429 || code == 408 || code >= 500) return true;
            if (code == 0) return true; // no HTTP status: network/IO problem
            return false; // 4xx (404 = file not in the pinned commit) will not change on retry
        }

        private static string DescribeError(UnityWebRequest request)
        {
            long code = request.responseCode;
            string error = string.IsNullOrEmpty(request.error) ? "unknown error" : request.error;
            return code > 0 ? $"HTTP {code} ({error})" : error;
        }

        // ================================================================================================ completion

        private void Complete()
        {
            string licenseProblem = null;
            try
            {
                WriteLicense(_commit);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                licenseProblem = "could not write " + RocketboxAssetSet.LicenseFileName + ": " + e.Message;
            }

            var failures = new List<string>(_preflightProblems);
            failures.AddRange(_failures);
            if (licenseProblem != null) failures.Add(licenseProblem);

            var sb = new StringBuilder();
            sb.Append($"Rocketbox download: {_downloaded} file(s) downloaded ({FormatBytes(_bytes)}), {_skipped} already present");
            if (failures.Count == 0)
            {
                sb.Append('.');
                Finish(true, sb.ToString(), true);
                return;
            }

            sb.Append($", {failures.Count} problem(s):");
            int shown = 0;
            foreach (string f in failures)
            {
                if (++shown > 12)
                {
                    sb.Append($"\n• … and {failures.Count - 12} more (see the Console).");
                    break;
                }
                sb.Append("\n• ").Append(f);
            }
            foreach (string f in failures) Debug.LogWarning("[Dodgeball Ultra] Rocketbox download: " + f);
            Finish(false, sb.ToString(), true);
        }

        private void Finish(bool success, string message, bool refresh)
        {
            if (_finished) return;
            _finished = true;
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            if (s_current == this) s_current = null;
            CleanTempFolder();

            // Import whatever arrived (also after a cancel/failure: completed files are valid and kept).
            if (refresh || _downloaded > 0)
            {
                try
                {
                    AssetDatabase.Refresh();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            try
            {
                _onProgress?.Invoke(success ? 1f : Progress, message);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            try
            {
                _onComplete?.Invoke(success, message);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void OnBeforeAssemblyReload()
        {
            // A domain reload destroys this object: release the native requests and temp files first.
            if (_finished) return;
            AbortAll();
            _finished = true;
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            if (s_current == this) s_current = null;
            CleanTempFolder();
            Debug.LogWarning("[Dodgeball Ultra] Rocketbox download interrupted by a script reload; run the download again to resume " +
                             "(files already downloaded are kept).");
        }

        private void AbortAll()
        {
            foreach (Active a in _active)
            {
                try
                {
                    a.Request.Abort();
                    a.Request.Dispose();
                }
                catch (Exception)
                {
                    // already disposed
                }
                TryDelete(a.PartPath);
            }
            _active.Clear();
            _waitingRetry.Clear();
            _pending.Clear();
        }

        // ================================================================================================= progress

        private float Progress
        {
            get
            {
                if (_total <= 0) return 1f;
                float inFlight = 0f;
                foreach (Active a in _active) inFlight += Mathf.Clamp01(a.Request.downloadProgress);
                return Mathf.Clamp01((_done + inFlight) / _total);
            }
        }

        private void Report(bool force)
        {
            if (_onProgress == null) return;
            double now = EditorApplication.timeSinceStartup;
            if (!force && now - _lastProgressTime < ProgressInterval) return;
            _lastProgressTime = now;

            long inFlightBytes = 0;
            foreach (Active a in _active) inFlightBytes += (long)a.Request.downloadedBytes;
            string current = string.IsNullOrEmpty(_lastFile) ? string.Empty : " · " + _lastFile;
            string retry = _waitingRetry.Count > 0 ? $" · {_waitingRetry.Count} retrying" : string.Empty;
            string message = $"Downloading Rocketbox files {_done}/{_total} ({FormatBytes(_bytes + inFlightBytes)}){current}{retry}";
            try
            {
                _onProgress(Progress, message);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // ====================================================================================================== IO

        private static void MoveIntoPlace(string partPath, string assetPath)
        {
            string target = RocketboxAssetSet.ToAbsolutePath(assetPath);
            string directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(target)) File.Delete(target);
            File.Move(partPath, target);
        }

        private static long SafeLength(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : -1;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return -1;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // A locked part file is removed by the next CleanTempFolder.
            }
        }

        private void CleanTempFolder()
        {
            try
            {
                if (!Directory.Exists(_tempFolder)) return;
                foreach (string file in Directory.GetFiles(_tempFolder, "*.part")) TryDelete(file);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // best effort
            }
        }

        /// <summary>Writes <c>LICENSE-Rocketbox.txt</c> (MIT, Copyright (c) 2020 Microsoft) into the download root.</summary>
        public static void WriteLicense(string commit)
        {
            string path = RocketboxAssetSet.ToAbsolutePath(CharacterPipeline.DownloadRoot + "/" + RocketboxAssetSet.LicenseFileName);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string text = LicenseText.Replace("{commit}", string.IsNullOrEmpty(commit) ? "unknown" : commit).Replace("\r\n", "\n");
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>True when the license file exists in the download root.</summary>
        public static bool LicenseExists()
            => RocketboxAssetSet.FileExistsNonEmpty(CharacterPipeline.DownloadRoot + "/" + RocketboxAssetSet.LicenseFileName);

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.00") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0") + " KB";
            return bytes + " B";
        }

        /// <summary>Same text as <c>Tools/fetch_rocketbox.py</c> (LICENSE_TEXT).</summary>
        private const string LicenseText = @"Microsoft Rocketbox Avatar Library
https://github.com/microsoft/Microsoft-Rocketbox

The avatars, textures and motion-capture animations in this folder were downloaded from the repository above
(pinned commit {commit}) and are distributed under the MIT License:

MIT License

Copyright (c) 2020 Microsoft

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Citation requested by the authors for research use:
M. Gonzalez-Franco et al., ""The Rocketbox library and the utility of freely available rigged avatars"",
Frontiers in Virtual Reality, 2020. DOI 10.3389/frvir.2020.561558
";
    }
}
