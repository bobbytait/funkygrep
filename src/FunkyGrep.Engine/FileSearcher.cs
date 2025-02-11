﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FunkyGrep.Engine.Collections;
using HeyRed.Mime;

namespace FunkyGrep.Engine;

public class FileSearcher
{
    public const long MaxFileSize = 256 * 1024 * 1024;
    public const int DefaultMaxContextLength = 512;

    readonly CancellationTokenSource _cancelSrc;

    readonly Regex _expression;
    readonly IEnumerable<IDataSource> _dataSources;
    readonly bool _skipBinaryFiles;
    readonly int _contextLineCount;

    readonly object _beginCancelLocker = new();
    readonly object _magicCreateLocker = new();
    readonly int _maxContextLength;
    long _doneCount;
    long _failedCount;
    long _skippedCount;
    Task? _progressReportTask;
    Task? _searchTask;

    // Progress tracking
    long _totalCount;

    public FileSearcher(
        Regex expression,
        IEnumerable<IDataSource> dataSources,
        bool skipBinaryFiles,
        int contextLineCount,
        int maxContextLength = DefaultMaxContextLength)
    {
        if (contextLineCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLineCount));
        }

        if (maxContextLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContextLength));
        }

        this._expression = expression ?? throw new ArgumentNullException(nameof(expression));
        this._dataSources = dataSources ?? throw new ArgumentNullException(nameof(dataSources));
        this._skipBinaryFiles = skipBinaryFiles;
        this._contextLineCount = contextLineCount;
        this._maxContextLength = maxContextLength;

        this._cancelSrc = new CancellationTokenSource();
    }

    public event EventHandler<ProgressEventArgs>? ProgressChanged;
    public event EventHandler<MatchFoundEventArgs>? MatchFound;
    public event EventHandler<SearchErrorEventArgs>? Error;
    public event EventHandler? Reset;
    public event EventHandler<CompletedEventArgs>? Completed;

    public void Begin()
    {
        lock (this._beginCancelLocker)
        {
            if (this._searchTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("Searcher task is already running.");
            }

            this._searchTask = Task.Run(this.DoSearchTask, this._cancelSrc.Token);

            // Find out the total number of files on a separate thread
            Task.Run(
                () =>
                {
                    try
                    {
                        int totalCount = 0;

                        using (IEnumerator<IDataSource> enumerator = this._dataSources.GetEnumerator())
                        {
                            while (enumerator.MoveNext())
                            {
                                if (this._cancelSrc.IsCancellationRequested)
                                {
                                    return;
                                }

                                totalCount++;
                            }
                        }

                        Interlocked.Exchange(ref this._totalCount, totalCount);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                });

            this._progressReportTask = Task.Run(
                () =>
                {
                    try
                    {
                        // Enter report loop
                        do
                        {
                            this.ProgressChanged?.Invoke(
                                this,
                                new ProgressEventArgs(
                                    Interlocked.Read(ref this._doneCount),
                                    Interlocked.Read(ref this._totalCount),
                                    Interlocked.Read(ref this._failedCount),
                                    Interlocked.Read(ref this._skippedCount)));
                        }
                        while (!this._cancelSrc.Token.WaitHandle.WaitOne(100)
                               && !this._searchTask.IsCompleted);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                },
                this._cancelSrc.Token);
        }
    }

    void DoSearchTask() => this.DoSearchTask(false);

    void DoSearchTask(bool disableParallelism)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? error = null;
        bool suppressFinally = false;

        try
        {
            this.RunSearch(disableParallelism, this._cancelSrc.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ex is AggregateException && ex.InnerException is IOException && !disableParallelism)
            {
                Interlocked.Exchange(ref this._doneCount, 0);
                Interlocked.Exchange(ref this._failedCount, 0);
                Interlocked.Exchange(ref this._skippedCount, 0);
                this.Reset?.Invoke(this, EventArgs.Empty);
                this.Error?.Invoke(
                    this,
                    new SearchErrorEventArgs(
                        "(general error)",
                        new Exception(
                            "Parallel search failed. Restarted search in sequential mode.",
                            ex.InnerException)));
                this.DoSearchTask(true);
                suppressFinally = true;
                return;
            }

            error = ex;
        }
        finally
        {
            if (!suppressFinally)
            {
                this.Completed?.Invoke(
                    this,
                    new CompletedEventArgs(
                        stopwatch.Elapsed,
                        new ProgressEventArgs(
                            Interlocked.Read(ref this._doneCount),
                            Interlocked.Read(ref this._totalCount),
                            Interlocked.Read(ref this._failedCount),
                            Interlocked.Read(ref this._skippedCount)),
                        error));
            }
        }
    }

    public void Cancel()
    {
        lock (this._beginCancelLocker)
        {
            if (this._searchTask == null)
            {
                return;
            }

            this._cancelSrc.Cancel();
            this.Wait();
        }
    }

    public void Wait()
    {
        try
        {
            Task.WaitAll(this._progressReportTask ?? Task.CompletedTask, this._searchTask ?? Task.CompletedTask);

            this._progressReportTask = null;
            this._searchTask = null;
        }
        catch (AggregateException aex)
        {
            aex.Handle(ex => ex is OperationCanceledException);
        }
        finally
        {
            this._progressReportTask = null;
            this._searchTask = null;
        }
    }

    [SuppressMessage("ReSharper", "HeapView.ClosureAllocation")]
    void RunSearch(bool disableParallelism, CancellationToken token)
    {
        var parallelOptions = new ParallelOptions { CancellationToken = token };
        if (disableParallelism)
        {
            parallelOptions.MaxDegreeOfParallelism = 1;
        }

        Parallel.ForEach(
            this._dataSources,
            parallelOptions,
            () => new
            {
                Regex = new Regex(this._expression.ToString(), this._expression.Options),
                Magic = this.CreateMagic()
            },
            (dataSource, _, _, vars) =>
            {
                List<SearchMatch>? matches = null;
                Exception? error = null;

                try
                {
                    var byteArrayPool = ArrayPool<byte>.Shared;

                    using var stream = dataSource.OpenRead();
                    var length = stream.Length;

                    if (length is > MaxFileSize or 0)
                    {
                        return vars;
                    }

                    token.ThrowIfCancellationRequested();

                    var byteBuffer = byteArrayPool.Rent(4096);
                    try
                    {
                        var bytesRead = stream.Read(byteBuffer, 0, byteBuffer.Length);
                        if (this._skipBinaryFiles && IsLikelyToBeBinary(byteBuffer, bytesRead, vars.Magic))
                        {
                            Interlocked.Increment(ref this._skippedCount);
                            return vars;
                        }
                    }
                    finally
                    {
                        byteArrayPool.Return(byteBuffer);
                    }

                    stream.Seek(0, SeekOrigin.Begin);

                    using var reader = new StreamReader(stream, true);

                    var lineBuffer = new CircularBuffer<string?>(this._contextLineCount * 2 + 1);
                    var postMatchLineCount = 0;
                    var readLineCount = 0;
                    string? lastReadLine;

                    // At the beginning of the file, there are no pre-match lines
                    for (int i = 0; i < this._contextLineCount; i++)
                    {
                        lineBuffer.PushBack(null);
                    }

                    // Read first line and post-match lines
                    var readFirstLine = false;
                    do
                    {
                        lastReadLine = reader.ReadLine();
                        token.ThrowIfCancellationRequested();

                        if (lastReadLine == null)
                        {
                            break;
                        }

                        if (!readFirstLine)
                        {
                            readFirstLine = true;
                        }
                        else
                        {
                            postMatchLineCount++;
                        }

                        readLineCount++;
                        lineBuffer.PushBack(lastReadLine);
                    }
                    while (!lineBuffer.IsFull);

                    Debug.Assert(lastReadLine == null != lineBuffer.IsFull);

                    while (!lineBuffer.IsFull)
                    {
                        lineBuffer.PushBack(null);
                    }

                    for (string? currentLine = lineBuffer[this._contextLineCount];
                         currentLine != null;
                         lastReadLine = reader.ReadLine(),
                         readLineCount += lastReadLine == null ? 0 : 1,
                         postMatchLineCount -= postMatchLineCount > 0 && lastReadLine == null ? 1 : 0,
                         lineBuffer.PushBack(lastReadLine),
                         currentLine = lineBuffer[this._contextLineCount])
                    {
                        // ReSharper disable once AccessToDisposedClosure - Dispose happens after all parallel iteration callbacks are done.
                        var regexMatches = vars.Regex.Matches(currentLine);
                        foreach (Match regexMatch in regexMatches)
                        {
                            if (!regexMatch.Success)
                            {
                                continue;
                            }

                            var match = this.GetMatch(
                                regexMatch,
                                lineBuffer,
                                readLineCount - postMatchLineCount);

                            matches ??= new List<SearchMatch>();
                            matches.Add(match);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref this._failedCount);
                    error = ex;
                }
                finally
                {
                    if (matches != null)
                    {
                        this.MatchFound?.Invoke(this, new MatchFoundEventArgs(dataSource.Identifier, matches));
                    }

                    if (error != null)
                    {
                        this.Error?.Invoke(this, new SearchErrorEventArgs(dataSource.Identifier, error));
                    }

                    Interlocked.Increment(ref this._doneCount);
                }

                return vars;
            },
            vars => vars.Magic.Dispose());
    }

    Magic CreateMagic()
    {
        // magic_load is not thread safe and alters static state.
        // Protect from heap corruption.
        lock (this._magicCreateLocker)
        {
            return new Magic(
                MagicOpenFlags.MAGIC_MIME_TYPE
                | MagicOpenFlags.MAGIC_ERROR
                | MagicOpenFlags.MAGIC_NO_CHECK_COMPRESS
                | MagicOpenFlags.MAGIC_NO_CHECK_APPTYPE
                | MagicOpenFlags.MAGIC_NO_CHECK_ELF
                | MagicOpenFlags.MAGIC_NO_CHECK_SOFT
                | MagicOpenFlags.MAGIC_NO_CHECK_TAR
                | MagicOpenFlags.MAGIC_NO_CHECK_TOKENS
                | MagicOpenFlags.MAGIC_NO_CHECK_JSON
                | MagicOpenFlags.MAGIC_NO_CHECK_CDF
                | MagicOpenFlags.MAGIC_PRESERVE_ATIME);
        }
    }

    static bool IsLikelyToBeBinary(byte[] byteBuffer, int bytesRead, Magic magic)
    {
        if (byteBuffer == null)
        {
            throw new ArgumentNullException(nameof(byteBuffer));
        }

        if (bytesRead > byteBuffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesRead));
        }

        if (magic == null)
        {
            throw new ArgumentNullException(nameof(magic));
        }

        var nullCount = 0;
        var twoConsecutiveNullsFound = false;

        for (var j = 0; j < bytesRead; j++)
        {
            if (byteBuffer[j] != 0)
            {
                continue;
            }

            nullCount++;

            if (twoConsecutiveNullsFound)
            {
                if (nullCount > 2)
                {
                    break;
                }

                continue;
            }

            if (bytesRead <= j + 1)
            {
                continue;
            }

            j++;

            if (byteBuffer[j] != 0)
            {
                continue;
            }

            nullCount++;
            twoConsecutiveNullsFound = true;
        }

        if (twoConsecutiveNullsFound && nullCount > 2)
        {
            return true;
        }

        // Fallback to using libmagic (slower)
        var mimeType = magic.Read(byteBuffer, bytesRead);
        return !mimeType.StartsWith("text/", StringComparison.Ordinal);
    }

    SearchMatch GetMatch(Capture match, CircularBuffer<string?> lineBuffer, int matchLineNumber)
    {
        string line = lineBuffer[this._contextLineCount] ?? string.Empty;
        var remainingContextCharCount = this._maxContextLength - match.Length;

        string context;
        int adjustedMatchIndex;

        if (remainingContextCharCount <= 0)
        {
            context = match.Value;
            adjustedMatchIndex = 0;
        }
        else
        {
            var contextStartIndex = match.Index;
            var contextEndIndex = contextStartIndex + match.Length - 1;
            int remainingContextCharHalfCount = remainingContextCharCount / 2;
            var newContextEndIndex = Math.Min(contextEndIndex + remainingContextCharHalfCount, line.Length - 1);
            int charsAddedToContextEndIndex = newContextEndIndex - contextEndIndex;
            contextEndIndex = newContextEndIndex;
            remainingContextCharCount -= charsAddedToContextEndIndex;
            var newContextStartIndex = Math.Max(0, contextStartIndex - remainingContextCharCount);
            int charsAddedToContextStartIndex = contextStartIndex - newContextStartIndex;
            contextStartIndex = newContextStartIndex;
            remainingContextCharCount -= charsAddedToContextStartIndex;

            if (remainingContextCharCount > 0)
            {
                contextEndIndex = Math.Min(contextEndIndex + remainingContextCharCount, line.Length - 1);
            }

            if (contextStartIndex == 0 && contextEndIndex == line.Length - 1)
            {
                context = line;
                adjustedMatchIndex = match.Index;
            }
            else
            {
                context = line[contextStartIndex..(contextEndIndex + 1)];
                adjustedMatchIndex = match.Index - contextStartIndex;
            }
        }

        List<string>? preMatchLines = null;
        List<string>? postMatchLines = null;

        void CaptureContextLines(int startIndex, ref List<string>? targetLines)
        {
            for (int i = startIndex; i < startIndex + this._contextLineCount; i++)
            {
                var contextLine = lineBuffer[i];

                if (contextLine == null)
                {
                    continue;
                }

                targetLines ??= new List<string>(this._contextLineCount);

                if (contextLine.Length > this._maxContextLength)
                {
                    contextLine = contextLine[..this._maxContextLength];
                }

                targetLines.Add(contextLine);
            }
        }

        CaptureContextLines(0, ref preMatchLines);
        CaptureContextLines(this._contextLineCount + 1, ref postMatchLines);

        return new SearchMatch(
            matchLineNumber,
            context,
            adjustedMatchIndex,
            match.Length,
            preMatchLines,
            postMatchLines);
    }
}
