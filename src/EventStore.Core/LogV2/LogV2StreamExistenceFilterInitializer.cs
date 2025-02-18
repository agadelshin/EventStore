﻿using System;
using System.Linq;
using EventStore.Common.Utils;
using EventStore.Core.Index;
using EventStore.Core.LogAbstraction;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.LogCommon;
using Serilog;

namespace EventStore.Core.LogV2 {
	/// <summary>
	/// Stream existence filter initializer for Log V2
	/// Reads the index and transaction log to populate the stream existence filter from the last checkpoint.
	/// May add a stream hash more than once.
	/// </summary>
	/// In V2 the the bloom filter checkpoint is the commit position of the last processed
	/// log record.
	public class LogV2StreamExistenceFilterInitializer : INameExistenceFilterInitializer {
		private readonly Func<TFReaderLease> _tfReaderFactory;
		private readonly ITableIndex _tableIndex;

		protected static readonly ILogger Log = Serilog.Log.ForContext<LogV2StreamExistenceFilterInitializer>();

		public LogV2StreamExistenceFilterInitializer(
			Func<TFReaderLease> tfReaderFactory,
			ITableIndex tableIndex) {

			Ensure.NotNull(tableIndex, nameof(tableIndex));

			_tfReaderFactory = tfReaderFactory;
			_tableIndex = tableIndex;
		}

		public void Initialize(INameExistenceFilter filter) {
			InitializeFromIndex(filter);
			InitializeFromLog(filter);
		}

		private void InitializeFromIndex(INameExistenceFilter filter) {
			if (filter.CurrentCheckpoint != -1L) {
				// can only use the index to build from scratch. if we have a checkpoint
				// we need to build from the log in order to make use of it.
				return;
			}

			Log.Information("Initializing from index");

			// we have no checkpoint, build from the index. unfortunately there isn't
			// a simple way to checkpoint in the middle of the index.
			var tables = _tableIndex.IterateAllInOrder().ToList();
			foreach (var table in tables) {
				if (table.Version == PTableVersions.IndexV1) {
					throw new NotSupportedException("The Stream Existence Filter is not supported with V1 index files. Please disable the filter by setting StreamExistenceFilterSize to 0, or rebuild the indexes.");
				}
			}

			ulong? previousHash = null;
			foreach (var table in tables) {
				foreach (var entry in table.IterateAllInOrder()) {
					if (entry.Stream == previousHash)
						continue;

					// add regardless of version because event 0 may be scavenged
					filter.Add(entry.Stream);
					previousHash = entry.Stream;
				}
			}

			// checkpoint at the end of the index.
			if (previousHash != null) {
				filter.CurrentCheckpoint = _tableIndex.CommitCheckpoint;
			}
		}

		private void InitializeFromLog(INameExistenceFilter filter) {
			// if we have a checkpoint, start from that position in the log. this will work
			// whether the checkpoint is the pre or post position of the last processed record.
			var startPosition = filter.CurrentCheckpoint == -1 ? 0 : filter.CurrentCheckpoint;
			Log.Information("Initializing from log starting at {startPosition:N0}", startPosition);
			using var reader = _tfReaderFactory();
			reader.Reposition(startPosition);

			while (TryReadNextLogRecord(reader, out var result)) {
				switch (result.LogRecord.RecordType) {
					case LogRecordType.Prepare:
						// add regardless of expectedVersion because event 0 may be scavenged
						// add regardless of committed or not because waiting for the commit is expensive
						var prepare = (IPrepareLogRecord<string>)result.LogRecord;
						filter.Add(prepare.EventStreamId);
						filter.CurrentCheckpoint = result.RecordPostPosition;
						break;
				}
			}
		}

		private static bool TryReadNextLogRecord(TFReaderLease reader, out SeqReadResult result) {
			result = reader.TryReadNext();
			return result.Success;
		}
	}
}
