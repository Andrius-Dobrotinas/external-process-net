﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.ExternalProcess
{
    public class ProcessRunner : IIOProcessRunner, IOutputOnlyProcessRunner
    {
        private readonly int exitTimeoutMs;
        private readonly int startWaitMs;
        private readonly int timeoutMs;
        private readonly bool showProcessWindowWithStdErrOutput;
        
        private const int ExitCode_CtrlC = -1073741510;
        public const int NoTimeoutValue = -1;

        /// <param name="timeoutSec">If a process doesn't finish within a given time (in seconds), it will be termined without returning any result</param>
        /// <param name="exitTimeoutMs">Time to wait (in milliseconds) for the process to exit after all of its stdout has been read. Shouldn't be a large value because most processes exit right after finishing to write to stdout.</param>
        /// <param name="startWaitMs">Time to wait (in milliseconds) before starting interacting with the process' std streams.
        /// Sometimes (depending on the speed of the computer?) it doesn't have std streams available right away, which results in "Pipe ended" error.</param>
        /// <param name="showProcessWindowWithStdErrOutput">When on, doesn't capture the process' stderror and therefore can't report errors - but the info is there for the user to see in window.
        /// When off, captures stderr and includes in exceptions if the process fails</param>
        public ProcessRunner(int timeoutSec, int exitTimeoutMs, int startWaitMs, bool showProcessWindowWithStdErrOutput)
        {
            this.timeoutMs = timeoutSec == NoTimeoutValue ? NoTimeoutValue : timeoutSec * 1000;
            this.exitTimeoutMs = exitTimeoutMs;
            this.startWaitMs = startWaitMs;
            this.showProcessWindowWithStdErrOutput = showProcessWindowWithStdErrOutput;
        }

        public ProcessOutputStream RunAndReadOutput(
            FileInfo executableFile,
            IEnumerable<string> arguments,
            CancellationToken cancellation = default)
        {
            var processSettings = ProcessStartInfoFactory.GetStandardProcessSettings(executableFile, arguments, showProcessWindowWithStdErrOutput);
            
            var process = new ExternalProcess { StartInfo = processSettings };

            return GetOutputStream_WaitProcessExitInParallel(process, input: null, process.StartInfo.RedirectStandardError, cancellation);
        }

        /* The idea is:
        * -start the process
        * -start reading std streams (can't start writing/reading to/from them until the process is running)
        * -wait until the process produces the output (std-out is fully read)
        * -get process exit code and error info/ if applicable
        * 
        * In addition to this, it also listens to cancellations and for time-out, which forces the killing of the process
        */
        public ProcessOutputStream RunAndReadOutput(
            FileInfo executableFile,
            IEnumerable<string> arguments,
            Stream inputData,
            CancellationToken cancellation = default)
        {
            if (inputData == null) throw new ArgumentNullException(nameof(inputData));

            var processSettings = ProcessStartInfoFactory.GetStandardProcessSettings(executableFile, arguments, showProcessWindowWithStdErrOutput);
            processSettings.RedirectStandardInput = true;

            var process = new ExternalProcess { StartInfo = processSettings };

            return GetOutputStream_WaitProcessExitInParallel(process, inputData, process.StartInfo.RedirectStandardError, cancellation);
        }

        public ProcessOutputStream GetOutputStream_WaitProcessExitInParallel(IExternalProcess process, Stream input = null, bool readStderr = false, CancellationToken cancellation = default)
        {
            process.Start();
            Task.Delay(startWaitMs).GetAwaiter().GetResult(); //throws a "Pipe ended" error when trying to write to std right away. Waiting a bit before writing seems to solve the problem, but this could be problematic if the system is slower...

            /* Error (progress) stream has to be actively read as when the buffer fills up, the process stops writing to std-out
             * (probably depends on whether stderr and stdout writes sequence or in parallel in the program).
             * The bigger the input file, the more is written to the error stream as progress report */
            CancellationTokenSource errorReadCancellation = null;
            Task<MemoryStream> stdErrorTask = null;
            if (readStderr)
            {
                errorReadCancellation = new CancellationTokenSource();
                stdErrorTask = Task.Run<MemoryStream>(() => ReadStreamCancellable(process.StandardError.BaseStream, errorReadCancellation.Token));
            }

            var writeTask = input != null
                ? Task.Run(() => WriteToStdInAndDisposeOf(process.StandardInput.BaseStream, input))
                : null;

            //I don't need a return value, but there's no non-generic version of this
            var outputReadTaskCompletion = new TaskCompletionSource<object>();

            var processWaitTask = Task.Run(() =>
            {
                WaitForOutputRead_AndProcessExitCode(process, outputReadTaskCompletion.Task, stdErrorTask, cancellation);
            });

            return new ProcessOutputStream(process.StandardOutput.BaseStream, outputReadTaskCompletion, processWaitTask);
        }

        /// <summary>
        /// Waits for output or error task to finish and processes the exit code.
        /// Also, handles cancellation and time-out.
        /// At the end, disposes of <paramref name="process"/>
        /// </summary>
        private void WaitForOutputRead_AndProcessExitCode(IExternalProcess process, Task outputReadTask, Task<MemoryStream> stdErrorTask = null, CancellationToken cancellation = default, CancellationTokenSource errorReadCancellation = null)
        {
            try
            {
                //wait for reading to finish or terminate the process on time-out or cancellation
                bool timedOut;
                try
                {
                    try
                    {
                        var finishedInTime = Task.WaitAll(
                            new[] { outputReadTask },
                            timeoutMs,
                            cancellation);

                        timedOut = !finishedInTime;
                    }
                    // This gets throw by WaitAll when one of the tasks it's waiting gets cancelled
                    // When WaitAll gets cancelled (via the cancellation token), it throws OperationCancelledException
                    catch (AggregateException e)
                    {
                        // Simply to unwrap the aggregate exception
                        throw e.InnerException;
                    }
                }
                catch (OperationCanceledException)
                {
                    // When exiting/getting killed, the process (normally) sends EOF to stdout and closes all streams 
                    process.Kill(true);
                    throw new OperationCanceledException("Process has been cancelled");
                }

                if (timedOut)
                {
                    //just in case it just finished
                    if (!process.HasExited)
                    {
                        // When exiting/getting killed, the process (normally) sends EOF to stdout and closes all streams 
                        process.Kill(true);
                        throw new TimeoutException("The process took taken longer than allowed and has been cancelled");
                    }
                    // The process exited before there was a chance to kill it (lucky)
                }

                // At this point, the std-out-read task is successfully finished and will not block.
                // It hasn't been cancelled, and hasn't timed-out.
                // Even if time-out had fired, it must've still finished before it got around to killing the process.

                ProcessExitCode(process, exitTimeoutMs, stdErrorTask, errorReadCancellation);
            }
            finally
            {
                process.Dispose();
            }
        }

        private static void WriteToStdInAndDisposeOf(Stream target, Stream inputData)
        {
            try
            {
                //This errors out when the process gets closed prematurely (timeout/cancellation) due to stdin getting closed/disposed of
                inputData.CopyTo(target);

                //it looks like either the stream has to be closed, or an "end of file" char (-1 in int language) must be written to the stream
                target.Close();
            }
            finally
            {
                inputData.Dispose();
            }
        }

        private static MemoryStream ReadStreamCancellable(Stream outputStream, CancellationToken cancellation = default)
        {
            var processOutput = new MemoryStream();

            //Just in case the stream freezes even after process exits (unlikely, but I've heard about such a problem).
            //Whatever has been copied over, should be returned because it can be useful even if it's incomplete.
            try
            {
                outputStream.CopyToAsync(processOutput, cancellation).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            processOutput.Seek(0, SeekOrigin.Begin);
            return processOutput;
        }

        private static void ProcessExitCode(IExternalProcess process, int exitTimeoutMs, Task<MemoryStream> stdErrorTask = null, CancellationTokenSource errorReadCancellation = null)
        {
            //sometimes it takes the process a while to quit after closing the std-out
            if (process.WaitForExit(exitTimeoutMs) == false)
                process.Kill(true);

            if (process.ExitCode != 0)
            {
                // This happens when this is run by a cmd-line application and it gets Ctrl+C'd as it relays the command to the spawned process (so far, return code was only confirmed on Windows)
                if (process.ExitCode == ExitCode_CtrlC)
                    throw new OperationCanceledException("Process has been cancelled");
                else if (stdErrorTask == null)
                    throw new ExecutionException(process.ExitCode);

                try
                {
                    // Wait for std-error read task to finish - or force-stop it on time-out
                    using (var stdError = WaitForErrorStream(stdErrorTask, exitTimeoutMs, errorReadCancellation))
                    {
                        string processErrorOutput = ReadErrorStreamOutput(stdError);
                        throw new ExecutionException(process.ExitCode, processErrorOutput);
                    }
                }
                catch (ExecutionException)
                {
                    throw;
                }
                catch
                {
                    throw new ExecutionException(process.ExitCode);
                }
            }
        }

        private static TStream WaitForErrorStream<TStream>(Task<TStream> stdErrorTask, int exitTimeoutMs, CancellationTokenSource errorReadCancellation)
        {
            bool finished = stdErrorTask.Wait(exitTimeoutMs);
            
            //Shouldn't ever happen, but just in case the stars align right
            if (!finished)
                errorReadCancellation.Cancel();

            //Even if cancelled, it will have captured stuff (anything up to cancellation)
            return stdErrorTask.GetAwaiter().GetResult();
        }

        private static string ReadErrorStreamOutput(Stream stdError)
        {
            using (var reader = new StreamReader(stdError))
            {
                return reader.ReadToEnd();
            }
        }
    }
}