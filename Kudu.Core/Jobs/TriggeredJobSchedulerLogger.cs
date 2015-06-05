using System;
using Kudu.Core.Tracing;

namespace Kudu.Core.Jobs
{
    public class TriggeredJobSchedulerLogger : FilePerJobLogger
    {
        public TriggeredJobSchedulerLogger(string jobName, IEnvironment environment, ITraceFactory traceFactory)
            : base(jobName, Constants.TriggeredPath, "status", "job_scheduler.log", environment, traceFactory)
        {
        }

        public override void LogStandardOutput(string message)
        {
            throw new NotSupportedException();
        }

        public override void LogStandardError(string message)
        {
            throw new NotSupportedException();
        }

        protected override void OnRolledLogFile()
        {
        }
    }
}
