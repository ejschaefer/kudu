using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Kudu.Contracts.Jobs;
using Kudu.Core.Hooks;
using Kudu.Core.Infrastructure;
using Kudu.Core.Tracing;

namespace Kudu.Core.Jobs
{
    public class TriggeredJobsScheduler : IDisposable
    {
        private const int CheckForWatcherTimeout = 30 * 1000;

        private readonly ITriggeredJobsManager _triggeredJobsManager;
        private readonly ITraceFactory _traceFactory;
        private readonly IAnalytics _analytics;
        private readonly IEnvironment _environment;

        private readonly Dictionary<string, TriggeredJobSchedule> _triggeredJobsSchedules = new Dictionary<string, TriggeredJobSchedule>(StringComparer.OrdinalIgnoreCase);

        private Timer _startFileWatcherTimer;
        private FileSystemWatcher _fileSystemWatcher;

        private readonly object _lockObject = new object();

        public TriggeredJobsScheduler(ITriggeredJobsManager triggeredJobsManager, ITraceFactory traceFactory, IAnalytics analytics, IEnvironment environment)
        {
            _triggeredJobsManager = triggeredJobsManager;
            _traceFactory = traceFactory;
            _analytics = analytics;
            _environment = environment;

            _startFileWatcherTimer = new Timer(StartWatcher);
            _startFileWatcherTimer.Change(0, Timeout.Infinite);
        }

        private void StartWatcher(object state)
        {
            try
            {
                /*while (!Debugger.IsAttached)
                {
                    Thread.Sleep(1000);
                }*/
                lock (_lockObject)
                {
                    // Check if there is a directory we can listen on
                    if (!FileSystemHelpers.DirectoryExists(_triggeredJobsManager.JobsBinariesPath))
                    {
                        // If not check again in 30 seconds
                        _startFileWatcherTimer.Change(CheckForWatcherTimeout, Timeout.Infinite);
                        return;
                    }

                    // Start file system watcher
                    _fileSystemWatcher = new FileSystemWatcher(_triggeredJobsManager.JobsBinariesPath, JobSettings.JobSettingsFileName);
                    _fileSystemWatcher.Created += OnChanged;
                    _fileSystemWatcher.Changed += OnChanged;
                    _fileSystemWatcher.Deleted += OnChanged;
                    _fileSystemWatcher.Renamed += OnChanged;
                    _fileSystemWatcher.Error += OnError;
                    _fileSystemWatcher.NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastWrite;
                    _fileSystemWatcher.IncludeSubdirectories = true;
                    _fileSystemWatcher.EnableRaisingEvents = true;

                    // Refresh all jobs
                    IEnumerable<TriggeredJob> triggeredJobs = _triggeredJobsManager.ListJobs();
                    foreach (TriggeredJob triggeredJob in triggeredJobs)
                    {
                        UpdateTriggeredJobSchedule(triggeredJob.Name, triggeredJob);
                    }
                }
            }
            catch (Exception ex)
            {
                _analytics.UnexpectedException(ex);

                // Retry in 30 seconds.
                _startFileWatcherTimer.Change(CheckForWatcherTimeout, Timeout.Infinite);
            }
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            Exception ex = e.GetException();
            _traceFactory.GetTracer().TraceError(ex.ToString());
            ResetWatcher();
        }

        private void ResetWatcher()
        {
            DisposeWatcher();

            _startFileWatcherTimer.Change(CheckForWatcherTimeout, Timeout.Infinite);
        }

        private void DisposeWatcher()
        {
            if (_fileSystemWatcher != null)
            {
                _fileSystemWatcher.EnableRaisingEvents = false;
                _fileSystemWatcher.Dispose();
                _fileSystemWatcher = null;
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                string path = e.FullPath;
                if (path != null && path.Length > _triggeredJobsManager.JobsBinariesPath.Length)
                {
                    path = path.Substring(_triggeredJobsManager.JobsBinariesPath.Length).TrimStart(Path.DirectorySeparatorChar);
                    int firstSeparator = path.IndexOf(Path.DirectorySeparatorChar);
                    string jobName;
                    if (firstSeparator >= 0)
                    {
                        jobName = path.Substring(0, firstSeparator);
                    }
                    else
                    {
                        if (e.ChangeType == WatcherChangeTypes.Changed)
                        {
                            return;
                        }

                        jobName = path;
                    }

                    if (!String.IsNullOrWhiteSpace(jobName))
                    {
                        lock (_lockObject)
                        {
                            UpdateTriggeredJobSchedule(jobName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _analytics.UnexpectedException(ex);

                _traceFactory.GetTracer().TraceError(ex.ToString());
            }
        }

        private void UpdateTriggeredJobSchedule(string jobName, TriggeredJob triggeredJob = null)
        {
            TriggeredJobSchedule triggeredJobSchedule;
            _triggeredJobsSchedules.TryGetValue(jobName, out triggeredJobSchedule);

            if (triggeredJob == null)
            {
                triggeredJob = _triggeredJobsManager.GetJob(jobName);
            }

            if (triggeredJob != null)
            {
                string cronExpression = triggeredJob.Settings != null ? triggeredJob.Settings.GetSchedule() : null;
                if (cronExpression != null)
                {
                    var logger = new TriggeredJobSchedulerLogger(triggeredJob.Name, _environment, _traceFactory);

                    Schedule schedule = Schedule.BuildSchedule(cronExpression, logger);
                    if (schedule != null)
                    {
                        if (triggeredJobSchedule == null)
                        {
                            triggeredJobSchedule = new TriggeredJobSchedule(triggeredJob, OnSchedule, logger);
                            _triggeredJobsSchedules[jobName] = triggeredJobSchedule;
                        }

                        DateTime lastRun = triggeredJob.LatestRun != null ? triggeredJob.LatestRun.StartTime : DateTime.MinValue;

                        triggeredJobSchedule.Reschedule(lastRun, schedule);

                        return;
                    }
                }
            }

            if (triggeredJobSchedule != null)
            {
                _traceFactory.GetTracer().Trace("Removing schedule for triggered WebJob {0}".FormatCurrentCulture(jobName));

                triggeredJobSchedule.Dispose();
                _triggeredJobsSchedules.Remove(jobName);
            }
        }

        private void OnSchedule(TriggeredJobSchedule triggeredJobSchedule)
        {
            try
            {
                string triggeredJobName = triggeredJobSchedule.TriggeredJob.Name;

                TriggeredJobRun latestTriggeredJobRun = _triggeredJobsManager.GetLatestJobRun(triggeredJobName);

                // Make sure we are on schedule
                // Check for the next occurence after the last run (as of now)
                // If it is still now, invoke the triggered job
                // If it's not now (in the future) reschedule the triggered job schedule starting with the last triggered job run
                TimeSpan currentSchedule = triggeredJobSchedule.Schedule.GetNextSchedule(latestTriggeredJobRun.StartTime);
                if (currentSchedule == TimeSpan.Zero)
                {
                    triggeredJobSchedule.Logger.LogInformation("Invoking WebJob");
                    _triggeredJobsManager.InvokeTriggeredJob(triggeredJobName, null);
                }
                else
                {
                    triggeredJobSchedule.Reschedule(latestTriggeredJobRun.StartTime);
                }
            }
            catch (ConflictException)
            {
                // Ignore as this is expected when running multiple instances
            }
            catch (Exception ex)
            {
                _traceFactory.GetTracer().TraceError(ex);
            }

            triggeredJobSchedule.Reschedule(DateTime.Now);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // HACK: Next if statement should be removed once ninject wlll not dispose this class
            // Since ninject automatically calls dispose we currently disable it
            if (disposing)
            {
                return;
            }
            // End of code to be removed

            if (disposing)
            {
                if (_startFileWatcherTimer != null)
                {
                    _startFileWatcherTimer.Dispose();
                    _startFileWatcherTimer = null;
                }

                DisposeWatcher();
            }
        }
    }
}