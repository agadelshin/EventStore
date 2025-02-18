using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.Replication;
using EventStore.Core.Services.Storage.EpochManager;
using EventStore.Core.Services.Transport.Tcp;
using EventStore.Core.Tests.Authentication;
using EventStore.Core.Tests.Authorization;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.Services.Transport.Tcp;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication {

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	[TestFixture(typeof(LogFormat.V3), typeof(uint))]
	public abstract class with_replication_service_and_epoch_manager<TLogFormat, TStreamId>  : SpecificationWithDirectoryPerTestFixture {
		private const int _connectionPendingSendBytesThreshold = 10 * 1024;
		private const int _connectionQueueSizeThreshold = 50000;

		protected int ClusterSize = 3;
		protected InMemoryBus Publisher = new InMemoryBus("publisher");
		protected InMemoryBus TcpSendPublisher = new InMemoryBus("tcpSend");
		protected LeaderReplicationService Service;
		protected ConcurrentQueue<TcpMessage.TcpSend> TcpSends = new ConcurrentQueue<TcpMessage.TcpSend>();
		private static readonly IRecordFactory<TStreamId> _recordFactory = LogFormatHelper<TLogFormat, TStreamId>.RecordFactory;
		protected Guid LeaderId = Guid.NewGuid();

		protected TFChunkDbConfig DbConfig;
		protected EpochManager EpochManager;
		protected TFChunkDb Db;
		protected TFChunkWriter Writer;

		[OneTimeSetUp]
		public override async Task TestFixtureSetUp() {
			await base.TestFixtureSetUp();
			TcpSendPublisher.Subscribe(new AdHocHandler<TcpMessage.TcpSend>(msg => TcpSends.Enqueue(msg)));

			DbConfig = CreateDbConfig();
			Db = new TFChunkDb(DbConfig);
			Db.Open();

			Writer = new TFChunkWriter(Db);
			EpochManager = new EpochManager(
				Publisher,
				5,
				DbConfig.EpochCheckpoint,
				Writer,
				1, 1,
				() => new TFChunkReader(Db, Db.Config.WriterCheckpoint,
					optimizeReadSideCache: Db.Config.OptimizeReadSideCache),
				_recordFactory,
				Guid.NewGuid());
			Service = new LeaderReplicationService(
				Publisher,
				LeaderId,
				Db,
				TcpSendPublisher,
				EpochManager,
				ClusterSize,
				false,
				new QueueStatsManager());

			Service.Handle(new SystemMessage.SystemStart());
			Service.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid(),0));

			When();
		}

		[OneTimeTearDown]
		public override async Task TestFixtureTearDown() {
			await base.TestFixtureTearDown();
			Service.Handle(new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), true, true));
		}

		public IPrepareLogRecord<TStreamId> CreateLogRecord(long eventNumber, string data = "*************") {
			var tStreamId = LogFormatHelper<TLogFormat, TStreamId>.StreamId;
			var eventType = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;
			return LogRecord.Prepare<TStreamId>(_recordFactory, Db.Config.WriterCheckpoint.ReadNonFlushed(), Guid.NewGuid(), Guid.NewGuid(), 0, 0,
				tStreamId, eventNumber, PrepareFlags.None, eventType, Encoding.UTF8.GetBytes(data),
				null, DateTime.UtcNow);
		}

		public Guid AddSubscription(Guid replicaId, bool isPromotable, Epoch[] epochs, long logPosition, out TcpConnectionManager manager) {
			var tcpConn = new DummyTcpConnection() { ConnectionId = replicaId };

			manager = new TcpConnectionManager(
				"Test Subscription Connection manager", TcpServiceType.External, new ClientTcpDispatcher(2_000),
				InMemoryBus.CreateTest(), tcpConn, InMemoryBus.CreateTest(),
				new InternalAuthenticationProvider(InMemoryBus.CreateTest(),
					new Core.Helpers.IODispatcher(InMemoryBus.CreateTest(), new NoopEnvelope()),
					new StubPasswordHashAlgorithm(), 1, false),
				new AuthorizationGateway(new TestAuthorizationProvider()), 
				TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), (man, err) => { },
				_connectionPendingSendBytesThreshold, _connectionQueueSizeThreshold);
			var subRequest = new ReplicationMessage.ReplicaSubscriptionRequest(
				Guid.NewGuid(),
				new NoopEnvelope(),
				manager,
				logPosition,
				Guid.NewGuid(),
				epochs,
				PortsHelper.GetLoopback(),
				LeaderId,
				replicaId,
				isPromotable);
			Service.Handle(subRequest);
			return tcpConn.ConnectionId;
		}

		public abstract void When();

		public TcpMessage.TcpSend[] GetTcpSendsFor(TcpConnectionManager connection) {
			var sentMessages = new List<TcpMessage.TcpSend>();
			while (TcpSends.TryDequeue(out var msg)) {
				if (msg.ConnectionManager == connection)
					sentMessages.Add(msg);
			}

			return sentMessages.ToArray();
		}

		private TFChunkDbConfig CreateDbConfig() {
			ICheckpoint writerChk = new InMemoryCheckpoint(Checkpoint.Writer);
			ICheckpoint chaserChk = new InMemoryCheckpoint(Checkpoint.Chaser);
			ICheckpoint epochChk = new InMemoryCheckpoint(Checkpoint.Epoch, initValue: -1);
			ICheckpoint proposalChk = new InMemoryCheckpoint(Checkpoint.Proposal, initValue: -1);
			ICheckpoint truncateChk = new InMemoryCheckpoint(Checkpoint.Truncate, initValue: -1);
			ICheckpoint replicationCheckpoint = new InMemoryCheckpoint(-1);
			ICheckpoint indexCheckpoint = new InMemoryCheckpoint(-1);
			ICheckpoint streamExistenceFilterCheckpoint = new InMemoryCheckpoint(-1);
			var nodeConfig = new TFChunkDbConfig(
				PathName, 
				new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
				TFConsts.ChunkSize,
				TFConsts.ChunksCacheSize,
				writerChk,
				chaserChk,
				epochChk,
				proposalChk,
				truncateChk,
				replicationCheckpoint,
				indexCheckpoint,
				streamExistenceFilterCheckpoint,
				Constants.TFChunkInitialReaderCountDefault,
				Constants.TFChunkMaxReaderCountDefault,
				true);
			return nodeConfig;
		}
	}
}
