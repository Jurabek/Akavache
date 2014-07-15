﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Akavache.Sqlite3.Internal;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Reactive;

namespace Akavache.Sqlite3
{
    class SqliteOperationQueue
    {
        readonly Queue<Tuple<OperationType, IEnumerable, object>> operationQueue = new Queue<Tuple<OperationType, IEnumerable, object>>();

        readonly BulkSelectSqliteOperation bulkSelectKey;
        readonly BulkSelectByTypeSqliteOperation bulkSelectType;
        readonly BulkInsertSqliteOperation bulkInsertKey;
        readonly BulkInvalidateSqliteOperation bulkInvalidateKey;
        readonly BulkInvalidateByTypeSqliteOperation bulkInvalidateType;
        readonly InvalidateAllSqliteOperation invalidateAll;
        readonly VacuumSqliteOperation vacuum;
        readonly GetKeysSqliteOperation getAllKeys;

        public SqliteOperationQueue(SQLiteConnection conn, IScheduler scheduler)
        {
            bulkSelectKey = new BulkSelectSqliteOperation(conn, false);
            bulkSelectType = new BulkSelectByTypeSqliteOperation(conn);
            bulkInsertKey = new BulkInsertSqliteOperation(conn);
            bulkInvalidateKey = new BulkInvalidateSqliteOperation(conn, false);
            bulkInvalidateType = new BulkInvalidateByTypeSqliteOperation(conn);
            invalidateAll = new InvalidateAllSqliteOperation(conn);
            vacuum = new VacuumSqliteOperation(conn, scheduler);
            getAllKeys = new GetKeysSqliteOperation(conn, scheduler);
        }

        public void ProcessItems()
        {
            var toProcess = new List<Tuple<OperationType, IEnumerable, object>>();
            lock (operationQueue) 
            {
                while (operationQueue.Count > 0 && toProcess.Count < Constants.OperationQueueChunkSize)
                {
                    if (operationQueue.Count == 0) break;
                    toProcess.Add(operationQueue.Dequeue());
                }
            }

            foreach (var item in toProcess) 
            {
                switch (item.Item1) {
                case OperationType.BulkInsertSqliteOperation:
                    MarshalCompletion(item.Item3, bulkInsertKey.PrepareToExecute((IEnumerable<CacheElement>)item.Item2));
                    break;
                case OperationType.BulkInvalidateByTypeSqliteOperation:
                    MarshalCompletion(item.Item3, bulkInvalidateType.PrepareToExecute((IEnumerable<string>)item.Item2));
                    break;
                case OperationType.BulkInvalidateSqliteOperation:
                    MarshalCompletion(item.Item3, bulkInvalidateKey.PrepareToExecute((IEnumerable<string>)item.Item2));
                    break;
                case OperationType.BulkSelectByTypeSqliteOperation:
                    MarshalCompletion(item.Item3, bulkSelectType.PrepareToExecute((IEnumerable<string>)item.Item2));
                    break;
                case OperationType.BulkSelectSqliteOperation:
                    MarshalCompletion(item.Item3, bulkSelectKey.PrepareToExecute((IEnumerable<string>)item.Item2));
                    break;
                case OperationType.GetKeysSqliteOperation:
                    MarshalCompletion(item.Item3, getAllKeys.PrepareToExecute());
                    break;
                case OperationType.InvalidateAllSqliteOperation:
                    MarshalCompletion(item.Item3, invalidateAll.PrepareToExecute());
                    break;
                case OperationType.VacuumSqliteOperation:
                    MarshalCompletion(item.Item3, vacuum.PrepareToExecute());
                    break;
                default:
                    throw new ArgumentException("Unknown operation");
                }
            }
        }

        public AsyncSubject<List<CacheElement>> Select(IEnumerable<string> keys)
        {
            var ret = new AsyncSubject<List<CacheElement>>();
            lock (operationQueue) 
                operationQueue.Enqueue(new Tuple<OperationType, IEnumerable, object>(OperationType.BulkSelectSqliteOperation, keys, ret));
            return ret;
        }

        public AsyncSubject<List<CacheElement>> SelectTypes(IEnumerable<string> types)
        {
            var ret = new AsyncSubject<List<CacheElement>>();
            lock (operationQueue) 
                operationQueue.Enqueue(new Tuple<OperationType, IEnumerable, object>(OperationType.BulkSelectByTypeSqliteOperation, types, ret));
            return ret;
        }

        public AsyncSubject<Unit> Insert(IEnumerable<CacheElement> items)
        {
            var ret = new AsyncSubject<Unit>();
            lock (operationQueue) 
                operationQueue.Enqueue(new Tuple<OperationType, IEnumerable, object>(OperationType.BulkInsertSqliteOperation, items, ret));
            return ret;
        }

        public AsyncSubject<Unit> Invalidate(IEnumerable<string> keys)
        {
            var ret = new AsyncSubject<Unit>();
            lock (operationQueue) 
                operationQueue.Enqueue(new Tuple<OperationType, IEnumerable, object>(OperationType.BulkInvalidateSqliteOperation, keys, ret));
            return ret;
        }

        public AsyncSubject<Unit> InvalidateTypes(IEnumerable<string> types)
        {
            var ret = new AsyncSubject<Unit>();
            lock (operationQueue) 
                operationQueue.Enqueue(new Tuple<OperationType, IEnumerable, object>(OperationType.BulkInvalidateByTypeSqliteOperation, types, ret));
            return ret;
        }

        public AsyncSubject<Unit> InvalidateAll()
        {
            var ret = new AsyncSubject<Unit>();
            lock (operationQueue) 
                operationQueue.Enqueue(new Tuple<OperationType, IEnumerable, object>(OperationType.InvalidateAllSqliteOperation, null, ret));
            return ret;
        }

        public AsyncSubject<Unit> Vacuum()
        {
            var ret = new AsyncSubject<Unit>();
            lock (operationQueue) 
                operationQueue.Enqueue(new Tuple<OperationType, IEnumerable, object>(OperationType.VacuumSqliteOperation, null, ret));
            return ret;
        }

        public AsyncSubject<List<string>> GetAllKeys()
        {
            var ret = new AsyncSubject<List<string>>();
            lock (operationQueue) 
                operationQueue.Enqueue(new Tuple<OperationType, IEnumerable, object>(OperationType.GetKeysSqliteOperation, null, ret));
            return ret;
        }

        void MarshalCompletion<T>(object completion, Func<T> block)
        {
            var subj = (AsyncSubject<T>)completion;
            try 
            {
                subj.OnNext(block());
                subj.OnCompleted();
            }
            catch (Exception ex)
            {
                subj.OnError(ex);
            }
        }

        void MarshalCompletion(object completion, Action block)
        {
            var subj = (AsyncSubject<Unit>)completion;
            try 
            {
                block();
                subj.OnNext(Unit.Default); subj.OnCompleted();
            }
            catch (Exception ex)
            {
                subj.OnError(ex);
            }
        }
    }
}