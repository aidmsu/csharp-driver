﻿//
//      Copyright (C) 2012-2014 DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Tasks;

// ReSharper disable DoNotCallOverridableMethodsInConstructor
// ReSharper disable CheckNamespace

namespace Cassandra
{
    /// <summary>
    /// Represents a result of a query returned by Cassandra.
    /// <para>
    /// The retrieval of the rows of a RowSet is generally paged (a first page
    /// of result is fetched and the next one is only fetched once all the results
    /// of the first one has been consumed). The size of the pages can be configured
    /// either globally through <see cref="QueryOptions.SetPageSize(int)"/> or per-statement
    /// with <see cref="IStatement.SetPageSize(int)"/>. Though new pages are automatically
    /// (and transparently) fetched when needed, it is possible to force the retrieval
    /// of the next page early through <see cref="FetchMoreResults()"/>.
    /// </para>
    /// <para>
    /// The RowSet dequeues <see cref="Row"/> items while iterated. Parallel enumerations 
    /// is supported and thread-safe. After a full enumeration of this instance, following
    /// enumerations will be empty, as all rows have been dequeued.
    /// </para>
    /// </summary>
    /// <remarks>
    /// RowSet paging is not available with the version 1 of the native protocol. 
    /// If the protocol version 1 is in use, a RowSet is always fetched in it's entirely and
    /// it's up to the client to make sure that no query can yield ResultSet that won't hold
    /// in memory.
    /// </remarks>
    public class RowSet : IEnumerable<Row>, IDisposable
    {
        private static readonly CqlColumn[] EmptyColumns = new CqlColumn[0];
        private volatile Func<byte[], Task<RowSet>> _fetchNextPage;
        private volatile byte[] _pagingState;
        private int _isPaging;
        private volatile Task _currentFetchNextPageTask;
        private volatile int _pageSyncAbortTimeout = Timeout.Infinite;

        /// <summary>
        /// Determines if when dequeuing, it will automatically fetch the following result pages.
        /// </summary>
        protected internal bool AutoPage { get; set; }

        /// <summary>
        /// Sets the method that is called to get the next page.
        /// </summary>
        internal void SetFetchNextPageHandler(Func<byte[], Task<RowSet>> handler, int pageSyncAbortTimeout)
        {
            if (_fetchNextPage != null)
            {
                throw new InvalidOperationException("Multiple sets to FetchNextPage not supported");
            }
            _fetchNextPage = handler;
            _pageSyncAbortTimeout = pageSyncAbortTimeout;
        }

        /// <summary>
        /// Gets or set the internal row list. It contains the rows of the latest query page.
        /// </summary>
        protected virtual ConcurrentQueue<Row> RowQueue { get; set; }

        /// <summary>
        /// Gets the amount of items in the internal queue. For testing purposes.
        /// </summary>
        internal int InnerQueueCount => RowQueue.Count;

        /// <summary>
        /// Gets the execution info of the query
        /// </summary>
        public virtual ExecutionInfo Info { get; set; }

        /// <summary>
        /// Gets or sets the columns in the RowSet
        /// </summary>
        public virtual CqlColumn[] Columns { get; set; }

        /// <summary>
        /// Gets or sets the paging state of the query for the RowSet.
        /// When set it states that there are more pages.
        /// </summary>
        public virtual byte[] PagingState
        {
            get => _pagingState;
            protected internal set => _pagingState = value;
        }

        /// <summary>
        /// Returns whether this ResultSet has more results.
        /// It has side-effects, if the internal queue has been consumed it will page for more results.
        /// </summary>
        /// <seealso cref="IsFullyFetched"/>
        public virtual bool IsExhausted()
        {
            if (RowQueue == null)
            {
                return true;
            }
            if (RowQueue.Count > 0)
            {
                return false;
            }
            PageNext();
            return RowQueue.Count == 0;
        }

        /// <summary>
        /// Whether all results from this result set has been fetched from the database.
        /// </summary>
        public virtual bool IsFullyFetched => PagingState == null || !AutoPage;

        /// <summary>
        /// Creates a new instance of RowSet.
        /// </summary>
        public RowSet() : this(false)
        {

        }

        /// <summary>
        /// Creates a new instance of RowSet.
        /// </summary>
        /// <param name="isVoid">Determines if the RowSet instance is created for a VOID result</param>
        private RowSet(bool isVoid)
        {
            if (!isVoid)
            {
                RowQueue = new ConcurrentQueue<Row>();
            }
            Info = new ExecutionInfo();
            Columns = EmptyColumns;
            AutoPage = true;
        }

        /// <summary>
        /// Returns a new RowSet instance without any columns or rows, designed for VOID results.
        /// </summary>
        internal static RowSet Empty()
        {
            return new RowSet(true);
        }

        /// <summary>
        /// Adds a row to the inner row list
        /// </summary>
        internal virtual void AddRow(Row row)
        {
            if (RowQueue == null)
            {
                throw new InvalidOperationException("Can not append a Row to a RowSet instance created for VOID results");
            }
            RowQueue.Enqueue(row);
        }

        /// <summary>
        /// Force the fetching the next page of results for this result set, if any.
        /// </summary>
        public void FetchMoreResults()
        {
            PageNext();
        }

        /// <summary>
        /// Force the fetching the next page of results without blocking for this result set, if any.
        /// </summary>
        public async Task FetchMoreResultsAsync()
        {
            var pageState = _pagingState;
            if (pageState == null || !AutoPage)
            {
                return;
            }

            // Only one concurrent call to page
            Task task;
            if (Interlocked.CompareExchange(ref _isPaging, 1, 0) != 0)
            {
                // Once isPaging flag is set, task will be set shortly
                var spin = new SpinWait();
                while ((task = _currentFetchNextPageTask) == null)
                {
                    // Use busy spin as the task should be set immediately after
                    // There is no risk on task being null after that
                    spin.SpinOnce();
                }

                await task.ConfigureAwait(false);
                return;
            }

            task = FetchAndEnqueueRows(pageState);
            _currentFetchNextPageTask = task;

            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _isPaging, 0);
            }
        }

        private async Task FetchAndEnqueueRows(byte[] pagingState)
        {
            var fetchMethod = _fetchNextPage ??
                              throw new DriverInternalError("Paging state set but delegate to retrieve is not");

            var rs = await fetchMethod(pagingState).ConfigureAwait(false);
            foreach (var newRow in rs.RowQueue)
            {
                RowQueue.Enqueue(newRow);
            }

            PagingState = rs.PagingState;
        }

        /// <summary>
        /// The number of rows available in this row set that can be retrieved without blocking to fetch.
        /// </summary>
        public int GetAvailableWithoutFetching()
        {
            return RowQueue?.Count ?? 0;
        }

        /// <summary>
        /// For backward compatibility: It is possible to iterate using the RowSet as it is enumerable.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<Row> GetRows()
        {
            //legacy: Keep the GetRows method for Compatibility.
            return this;
        }

        public virtual IEnumerator<Row> GetEnumerator()
        {
            if (RowQueue == null)
            {
                yield break;
            }
            while (!IsExhausted())
            {
                Row row;
                while (RowQueue.TryDequeue(out row))
                {
                    yield return row;
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Gets the next results and add the rows to the current RowSet queue
        /// </summary>
        protected virtual void PageNext()
        {
            TaskHelper.WaitToComplete(FetchMoreResultsAsync(), _pageSyncAbortTimeout);
        }

        /// <summary>
        /// For backward compatibility only
        /// </summary>
        [Obsolete("Explicitly releasing the RowSet resources is not required. It will be removed in future versions.", false)]
        public void Dispose()
        {

        }
    }
}
