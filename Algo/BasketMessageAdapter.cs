#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.Algo.Algo
File: BasketMessageAdapter.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License
namespace StockSharp.Algo
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using Ecng.Collections;
	using Ecng.Common;
	using Ecng.ComponentModel;
	using Ecng.Serialization;

	using MoreLinq;

	using StockSharp.Algo.Candles;
	using StockSharp.Algo.Candles.Compression;
	using StockSharp.Algo.Commissions;
	using StockSharp.Algo.Latency;
	using StockSharp.Algo.PnL;
	using StockSharp.Algo.Slippage;
	using StockSharp.Algo.Storages;
	using StockSharp.Algo.Testing;
	using StockSharp.Logging;
	using StockSharp.Messages;
	using StockSharp.Localization;

	/// <summary>
	/// The interface describing the list of adapters to trading systems with which the aggregator operates.
	/// </summary>
	public interface IInnerAdapterList : ISynchronizedCollection<IMessageAdapter>, INotifyList<IMessageAdapter>
	{
		/// <summary>
		/// Internal adapters sorted by operation speed.
		/// </summary>
		IEnumerable<IMessageAdapter> SortedAdapters { get; }

		/// <summary>
		/// The indexer through which speed priorities (the smaller the value, then adapter is faster) for internal adapters are set.
		/// </summary>
		/// <param name="adapter">The internal adapter.</param>
		/// <returns>The adapter priority. If the -1 value is set the adapter is considered to be off.</returns>
		int this[IMessageAdapter adapter] { get; set; }
	}

	/// <summary>
	/// Adapter-aggregator that allows simultaneously to operate multiple adapters connected to different trading systems.
	/// </summary>
	public class BasketMessageAdapter : BaseLogReceiver, IMessageAdapter
	{
		private sealed class InnerAdapterList : CachedSynchronizedList<IMessageAdapter>, IInnerAdapterList
		{
			private readonly BasketMessageAdapter _parent;
			private readonly Dictionary<IMessageAdapter, int> _enables = new Dictionary<IMessageAdapter, int>();

			public InnerAdapterList(BasketMessageAdapter parent)
			{
				_parent = parent ?? throw new ArgumentNullException(nameof(parent));
			}

			public IEnumerable<IMessageAdapter> SortedAdapters => Cache.Where(t => this[t] != -1).OrderBy(t => this[t]);

			protected override bool OnAdding(IMessageAdapter item)
			{
				_enables.Add(item, 0);
				return base.OnAdding(item);
			}

			protected override bool OnInserting(int index, IMessageAdapter item)
			{
				_enables.Add(item, 0);
				return base.OnInserting(index, item);
			}

			protected override bool OnRemoving(IMessageAdapter item)
			{
				_enables.Remove(item);
				_parent._adapterWrappers.Remove(item);

				lock (_parent._connectedResponseLock)
					_parent._adapterStates.Remove(item);

				return base.OnRemoving(item);
			}

			protected override bool OnClearing()
			{
				_enables.Clear();
				_parent._adapterWrappers.Clear();

				lock (_parent._connectedResponseLock)
					_parent._adapterStates.Clear();

				return base.OnClearing();
			}

			public int this[IMessageAdapter adapter]
			{
				get
				{
					lock (SyncRoot)
						return _enables.TryGetValue2(adapter) ?? -1;
				}
				set
				{
					if (value < -1)
						throw new ArgumentOutOfRangeException();

					lock (SyncRoot)
					{
						if (!Contains(adapter))
							Add(adapter);

						_enables[adapter] = value;
					}
				}
			}
		}

		private class ParentChildMap
		{
			private readonly SyncObject _syncObject = new SyncObject();
			private readonly Dictionary<long, RefTriple<long, SubscriptionStates, IMessageAdapter>> _childToParentIds = new Dictionary<long, RefTriple<long, SubscriptionStates, IMessageAdapter>>();

			public void AddMapping(long childId, ISubscriptionMessage parentMsg, IMessageAdapter adapter)
			{
				if (childId <= 0)
					throw new ArgumentOutOfRangeException(nameof(childId));

				if (parentMsg == null)
					throw new ArgumentNullException(nameof(parentMsg));

				if (adapter == null)
					throw new ArgumentNullException(nameof(adapter));

				lock (_syncObject)
					_childToParentIds.Add(childId, RefTuple.Create(parentMsg.TransactionId, SubscriptionStates.Stopped, adapter));
			}

			public IDictionary<long, IMessageAdapter> GetChild(long parentId)
			{
				if (parentId <= 0)
					throw new ArgumentOutOfRangeException(nameof(parentId));

				lock (_syncObject)
					return FilterByParent(parentId).Where(p => p.Value.Second.IsActive()).ToDictionary(p => p.Key, p => p.Value.Third);
			}

			private IEnumerable<KeyValuePair<long, RefTriple<long, SubscriptionStates, IMessageAdapter>>> FilterByParent(long parentId) => _childToParentIds.Where(p => p.Value.First == parentId);

			public long? ProcessChildResponse(long childId, bool isOk, out bool needParentResponse, out bool allError)
			{
				allError = true;
				needParentResponse = true;

				if (childId == 0)
					return null;

				lock (_syncObject)
				{
					if (!_childToParentIds.TryGetValue(childId, out var tuple))
						return null;
					
					var parentId = tuple.First;
					tuple.Second = isOk ? SubscriptionStates.Active : SubscriptionStates.Error;

					foreach (var pair in FilterByParent(parentId))
					{
						var t = pair.Value;

						// one of adapter still not yet response.
						if (t.Second == SubscriptionStates.Stopped)
						{
							needParentResponse = false;
							break;
						}
						
						if (t.Second != SubscriptionStates.Error)
							allError = false;
					}

					return parentId;
				}
			}

			public long? ProcessChildFinish(long childId, out bool needParentResponse)
				=> ProcessChild(childId, SubscriptionStates.Finished, out needParentResponse);

			public long? ProcessChildOnline(long childId, out bool needParentResponse)
				=> ProcessChild(childId, SubscriptionStates.Online, out needParentResponse);

			private long? ProcessChild(long childId, SubscriptionStates state, out bool needParentResponse)
			{
				needParentResponse = true;

				lock (_syncObject)
				{
					if (!_childToParentIds.TryGetValue(childId, out var tuple))
						return null;
					
					var parentId = tuple.First;
					tuple.Second = state;

					foreach (var pair in FilterByParent(parentId))
					{
						var t = pair.Value;

						if (t.Second != SubscriptionStates.Error && t.Second != state)
						{
							needParentResponse = false;
							break;
						}
					}

					return parentId;
				}
			}

			public void Clear()
			{
				lock (_syncObject)
					_childToParentIds.Clear();
			}

			public long? TryGetParent(long childId)
			{
				lock (_syncObject)
					return _childToParentIds.TryGetValue(childId)?.First;
			}
		}

		private readonly Dictionary<long, HashSet<IMessageAdapter>> _nonSupportedAdapters = new Dictionary<long, HashSet<IMessageAdapter>>();
		private readonly CachedSynchronizedDictionary<IMessageAdapter, IMessageAdapter> _adapterWrappers = new CachedSynchronizedDictionary<IMessageAdapter, IMessageAdapter>();
		private readonly SyncObject _connectedResponseLock = new SyncObject();
		private readonly Dictionary<MessageTypes, CachedSynchronizedSet<IMessageAdapter>> _messageTypeAdapters = new Dictionary<MessageTypes, CachedSynchronizedSet<IMessageAdapter>>();
		private readonly Queue<Message> _pendingMessages = new Queue<Message>();
		
		private readonly Dictionary<IMessageAdapter, Tuple<ConnectionStates, Exception>> _adapterStates = new Dictionary<IMessageAdapter, Tuple<ConnectionStates, Exception>>();
		private ConnectionStates _currState = ConnectionStates.Disconnected;

		private readonly SynchronizedDictionary<string, IMessageAdapter> _portfolioAdapters = new SynchronizedDictionary<string, IMessageAdapter>(StringComparer.InvariantCultureIgnoreCase);
		private readonly SynchronizedDictionary<Tuple<SecurityId, MarketDataTypes?>, IMessageAdapter> _securityAdapters = new SynchronizedDictionary<Tuple<SecurityId, MarketDataTypes?>, IMessageAdapter>();

		private readonly SynchronizedDictionary<long, Tuple<ISubscriptionMessage, IMessageAdapter[], DataType>> _subscription = new SynchronizedDictionary<long, Tuple<ISubscriptionMessage, IMessageAdapter[], DataType>>();
		private readonly SynchronizedDictionary<long, Tuple<ISubscriptionMessage, IMessageAdapter>> _requestsById = new SynchronizedDictionary<long, Tuple<ISubscriptionMessage, IMessageAdapter>>();
		private readonly ParentChildMap _parentChildMap = new ParentChildMap();

		private readonly SynchronizedDictionary<long, IMessageAdapter> _orderAdapters = new SynchronizedDictionary<long, IMessageAdapter>();

		/// <summary>
		/// Initializes a new instance of the <see cref="BasketMessageAdapter"/>.
		/// </summary>
		/// <param name="transactionIdGenerator">Transaction id generator.</param>
		/// <param name="candleBuilderProvider">Candle builders provider.</param>
		public BasketMessageAdapter(IdGenerator transactionIdGenerator, CandleBuilderProvider candleBuilderProvider)
			: this(transactionIdGenerator, new InMemorySecurityMessageAdapterProvider(), new InMemoryPortfolioMessageAdapterProvider(), candleBuilderProvider, null, null)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="BasketMessageAdapter"/>.
		/// </summary>
		/// <param name="transactionIdGenerator">Transaction id generator.</param>
		/// <param name="securityAdapterProvider">The security based message adapter's provider.</param>
		/// <param name="portfolioAdapterProvider">The portfolio based message adapter's provider.</param>
		/// <param name="candleBuilderProvider">Candle builders provider.</param>
		/// <param name="storageRegistry">The storage of market data.</param>
		/// <param name="snapshotRegistry">Snapshot storage registry.</param>
		public BasketMessageAdapter(IdGenerator transactionIdGenerator,
			ISecurityMessageAdapterProvider securityAdapterProvider,
			IPortfolioMessageAdapterProvider portfolioAdapterProvider,
			CandleBuilderProvider candleBuilderProvider,
			IStorageRegistry storageRegistry,
			SnapshotRegistry snapshotRegistry)

		{
			TransactionIdGenerator = transactionIdGenerator ?? throw new ArgumentNullException(nameof(transactionIdGenerator));
			_innerAdapters = new InnerAdapterList(this);
			SecurityAdapterProvider = securityAdapterProvider ?? throw new ArgumentNullException(nameof(securityAdapterProvider));
			PortfolioAdapterProvider = portfolioAdapterProvider ?? throw new ArgumentNullException(nameof(portfolioAdapterProvider));
			CandleBuilderProvider = candleBuilderProvider ?? throw new ArgumentNullException(nameof(portfolioAdapterProvider));

			StorageRegistry = storageRegistry;// ?? throw new ArgumentNullException(nameof(storageRegistry));
			SnapshotRegistry = snapshotRegistry;// ?? throw new ArgumentNullException(nameof(snapshotRegistry));

			LatencyManager = new LatencyManager();
			CommissionManager = new CommissionManager();
			//PnLManager = new PnLManager();
			SlippageManager = new SlippageManager();

			SecurityAdapterProvider.Changed += SecurityAdapterProviderOnChanged;
			PortfolioAdapterProvider.Changed += PortfolioAdapterProviderOnChanged;
		}

		/// <summary>
		/// The portfolio based message adapter's provider.
		/// </summary>
		public IPortfolioMessageAdapterProvider PortfolioAdapterProvider { get; }

		/// <summary>
		/// The security based message adapter's provider.
		/// </summary>
		public ISecurityMessageAdapterProvider SecurityAdapterProvider { get; }

		/// <summary>
		/// Candle builders provider.
		/// </summary>
		public CandleBuilderProvider CandleBuilderProvider { get; }

		private readonly InnerAdapterList _innerAdapters;

		/// <summary>
		/// Adapters with which the aggregator operates.
		/// </summary>
		public IInnerAdapterList InnerAdapters => _innerAdapters;

		private INativeIdStorage _nativeIdStorage = new InMemoryNativeIdStorage();

		/// <summary>
		/// Security native identifier storage.
		/// </summary>
		public INativeIdStorage NativeIdStorage
		{
			get => _nativeIdStorage;
			set => _nativeIdStorage = value ?? throw new ArgumentNullException(nameof(value));
		}

		private ISecurityMappingStorage _securityMappingStorage;

		/// <summary>
		/// Security identifier mappings storage.
		/// </summary>
		public ISecurityMappingStorage SecurityMappingStorage
		{
			get => _securityMappingStorage;
			set => _securityMappingStorage = value ?? throw new ArgumentNullException(nameof(value));
		}

		/// <summary>
		/// Extended info <see cref="Message.ExtensionInfo"/> storage.
		/// </summary>
		public IExtendedInfoStorage ExtendedInfoStorage { get; set; }

		/// <summary>
		/// Orders registration delay calculation manager.
		/// </summary>
		public ILatencyManager LatencyManager { get; set; }

		/// <summary>
		/// The profit-loss manager.
		/// </summary>
		public IPnLManager PnLManager { get; set; }

		/// <summary>
		/// The commission calculating manager.
		/// </summary>
		public ICommissionManager CommissionManager { get; set; }

		/// <summary>
		/// Slippage manager.
		/// </summary>
		public ISlippageManager SlippageManager { get; set; }

		private IdGenerator _transactionIdGenerator;

		/// <inheritdoc />
		public IdGenerator TransactionIdGenerator
		{
			get => _transactionIdGenerator;
			set => _transactionIdGenerator = value ?? throw new ArgumentNullException(nameof(value));
		}

		IEnumerable<MessageTypeInfo> IMessageAdapter.PossibleSupportedMessages
		{
			get => GetSortedAdapters().SelectMany(a => a.PossibleSupportedMessages).DistinctBy(i => i.Type);
			set { }
		}

		IEnumerable<MessageTypes> IMessageAdapter.SupportedInMessages
		{
			get => GetSortedAdapters().SelectMany(a => a.SupportedInMessages).Distinct();
			set { }
		}

		IEnumerable<MessageTypes> IMessageAdapter.SupportedOutMessages
		{
			get => GetSortedAdapters().SelectMany(a => a.SupportedOutMessages).Distinct();
			set { }
		}

		IEnumerable<MessageTypes> IMessageAdapter.SupportedResultMessages
		{
			get => GetSortedAdapters().SelectMany(a => a.SupportedResultMessages).Distinct();
			set { }
		}

		/// <inheritdoc />
		IEnumerable<MarketDataTypes> IMessageAdapter.SupportedMarketDataTypes
		{
			get => GetSortedAdapters().SelectMany(a => a.SupportedMarketDataTypes).Distinct();
			set { }
		}

		IDictionary<string, RefPair<SecurityTypes, string>> IMessageAdapter.SecurityClassInfo { get; } = new Dictionary<string, RefPair<SecurityTypes, string>>();

		IEnumerable<Level1Fields> IMessageAdapter.CandlesBuildFrom => GetSortedAdapters().SelectMany(a => a.CandlesBuildFrom).Distinct();

		bool IMessageAdapter.CheckTimeFrameByRequest => false;

		ReConnectionSettings IMessageAdapter.ReConnectionSettings { get; } = new ReConnectionSettings();

		TimeSpan IMessageAdapter.HeartbeatInterval { get; set; }

		string IMessageAdapter.StorageName => nameof(BasketMessageAdapter).Remove(nameof(MessageAdapter));

		bool IMessageAdapter.IsNativeIdentifiersPersistable => false;

		bool IMessageAdapter.IsNativeIdentifiers => false;

		bool IMessageAdapter.IsFullCandlesOnly => GetSortedAdapters().All(a => a.IsFullCandlesOnly);

		bool IMessageAdapter.IsSupportSubscriptions => true;

		bool IMessageAdapter.IsSupportCandlesUpdates => GetSortedAdapters().Any(a => a.IsSupportCandlesUpdates);

		IEnumerable<Tuple<string, Type>> IMessageAdapter.SecurityExtendedFields => GetSortedAdapters().SelectMany(a => a.SecurityExtendedFields).Distinct();

		IEnumerable<int> IMessageAdapter.SupportedOrderBookDepths => GetSortedAdapters().SelectMany(a => a.SupportedOrderBookDepths).Distinct().OrderBy();

		bool IMessageAdapter.IsSupportOrderBookIncrements => GetSortedAdapters().Any(a => a.IsSupportOrderBookIncrements);

		bool IMessageAdapter.IsSupportExecutionsPnL => GetSortedAdapters().Any(a => a.IsSupportExecutionsPnL);

		MessageAdapterCategories IMessageAdapter.Categories => GetSortedAdapters().Select(a => a.Categories).JoinMask();

		OrderCancelVolumeRequireTypes? IMessageAdapter.OrderCancelVolumeRequired => GetSortedAdapters().FirstOrDefault()?.OrderCancelVolumeRequired;

		Type IMessageAdapter.OrderConditionType => null;
		
		bool IMessageAdapter.HeartbeatBeforConnect => false;

		Uri IMessageAdapter.Icon => GetType().GetIconUrl();

		bool IMessageAdapter.IsAutoReplyOnTransactonalUnsubscription => GetSortedAdapters().All(a => a.IsAutoReplyOnTransactonalUnsubscription);

		IOrderLogMarketDepthBuilder IMessageAdapter.CreateOrderLogMarketDepthBuilder(SecurityId securityId)
			=> new OrderLogMarketDepthBuilder(securityId);

		IEnumerable<object> IMessageAdapter.GetCandleArgs(Type candleType, SecurityId securityId, DateTimeOffset? from, DateTimeOffset? to)
			=> GetSortedAdapters().SelectMany(a => a.GetCandleArgs(candleType, securityId, from, to)).Distinct().OrderBy();

		TimeSpan IMessageAdapter.GetHistoryStepSize(DataType dataType, out TimeSpan iterationInterval)
		{
			foreach (var adapter in GetSortedAdapters())
			{
				var step = adapter.GetHistoryStepSize(dataType, out iterationInterval);

				if (step > TimeSpan.Zero)
					return step;
			}

			iterationInterval = TimeSpan.Zero;
			return TimeSpan.Zero;
		}

		bool IMessageAdapter.IsAllDownloadingSupported(DataType dataType) => GetSortedAdapters().Any(a => a.IsAllDownloadingSupported(dataType));
		
		bool IMessageAdapter.IsSecurityRequired(DataType dataType) => GetSortedAdapters().Any(a => a.IsSecurityRequired(dataType));

		/// <inheritdoc />
		public bool IsSecurityNewsOnly => GetSortedAdapters().All(a => a.IsSecurityNewsOnly);

		/// <summary>
		/// Restore subscription on reconnect.
		/// </summary>
		/// <remarks>
		/// Error case like connection lost etc.
		/// </remarks>
		public bool IsRestoreSubscriptionOnErrorReconnect { get; set; } = true;

		/// <summary>
		/// Suppress reconnecting errors.
		/// </summary>
		public bool SuppressReconnectingErrors { get; set; } = true;

		/// <summary>
		/// Use <see cref="CandleBuilderMessageAdapter"/>.
		/// </summary>
		public bool SupportCandlesCompression { get; set; } = true;

		/// <summary>
		/// Use <see cref="OrderLogMessageAdapter"/>.
		/// </summary>
		public bool SupportBuildingFromOrderLog { get; set; } = true;

		/// <summary>
		/// Use <see cref="OrderBookTruncateMessageAdapter"/>.
		/// </summary>
		public bool SupportOrderBookTruncate { get; set; } = true;

		/// <summary>
		/// Use <see cref="PartialDownloadMessageAdapter"/>.
		/// </summary>
		public bool SupportPartialDownload { get; set; } = true;

		/// <summary>
		/// Use <see cref="LookupTrackingMessageAdapter"/>.
		/// </summary>
		public bool SupportLookupTracking { get; set; } = true;

		/// <summary>
		/// Use <see cref="OfflineMessageAdapter"/>.
		/// </summary>
		public bool SupportOffline { get; set; }

		/// <summary>
		/// Do not add extra adapters.
		/// </summary>
		public bool IgnoreExtraAdapters { get; set; }

		/// <summary>
		/// To call the <see cref="ConnectMessage"/> event when the first adapter connects to <see cref="InnerAdapters"/>.
		/// </summary>
		public bool ConnectDisconnectEventOnFirstAdapter { get; set; } = true;

		/// <summary>
		/// The storage of market data.
		/// </summary>
		public IStorageRegistry StorageRegistry { get; }

		/// <summary>
		/// Snapshot storage registry.
		/// </summary>
		public SnapshotRegistry SnapshotRegistry { get; }

		/// <summary>
		/// Save data only for subscriptions.
		/// </summary>
		public bool StorageFilterSubscription { get; set; }

		/// <summary>
		/// The storage (database, file etc.).
		/// </summary>
		public IMarketDataDrive StorageDrive { get; set; }

		/// <summary>
		/// Storage mode. By default is <see cref="StorageModes.Incremental"/>.
		/// </summary>
		public StorageModes StorageMode { get; set; } = StorageModes.Incremental;

		/// <summary>
		/// Format.
		/// </summary>
		public StorageFormats StorageFormat { get; set; } = StorageFormats.Binary;

		private TimeSpan _storageDaysLoad;

		/// <summary>
		/// Max days to load stored data.
		/// </summary>
		public TimeSpan StorageDaysLoad
		{
			get => _storageDaysLoad;
			set
			{
				if (value < TimeSpan.Zero)
					throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.Str1219);

				_storageDaysLoad = value;
			}
		}

		/// <summary>
		/// Use separated <see cref="IMessageChannel"/> for each adapters.
		/// </summary>
		public bool UseSeparatedChannels { get; set; }

		/// <summary>
		/// To get adapters <see cref="IInnerAdapterList.SortedAdapters"/> sorted by the specified priority. By default, there is no sorting.
		/// </summary>
		/// <returns>Sorted adapters.</returns>
		protected IEnumerable<IMessageAdapter> GetSortedAdapters() => _innerAdapters.SortedAdapters;

		private IMessageAdapter[] Wrappers => _adapterWrappers.CachedValues;

		private void ProcessReset(ResetMessage message, bool isConnect)
		{
			Wrappers.ForEach(a =>
			{
				a.SendInMessage(message);
				a.Dispose();
			});

			_adapterWrappers.Clear();

			lock (_connectedResponseLock)
			{
				_messageTypeAdapters.Clear();

				if (!isConnect)
					_pendingMessages.Clear();

				_nonSupportedAdapters.Clear();

				_adapterStates.Clear();
				_currState = ConnectionStates.Disconnected;
			}

			_requestsById.Clear();
			_subscription.Clear();
			_parentChildMap.Clear();
		}

		private IMessageAdapter CreateWrappers(IMessageAdapter adapter)
		{
			var first = adapter;

			IMessageAdapter ApplyOwnInner(MessageAdapterWrapper a)
			{
				a.OwnInnerAdapter = first != adapter;
				return a;
			}

			if (IsHeartbeatOn(adapter))
			{
				adapter = ApplyOwnInner(new HeartbeatMessageAdapter(adapter)
				{
					SuppressReconnectingErrors = SuppressReconnectingErrors,
					Parent = this,
				});
			}

			if (SupportOffline)
				adapter = ApplyOwnInner(new OfflineMessageAdapter(adapter));

			if (IgnoreExtraAdapters)
				return adapter;

			if (UseSeparatedChannels)
			{
				adapter = ApplyOwnInner(new ChannelMessageAdapter(adapter,
					new InMemoryMessageChannel(new MessageByOrderQueue(), $"{adapter} In", SendOutError), 
					new InMemoryMessageChannel(new MessageByOrderQueue(), $"{adapter} Out", SendOutError)));
			}

			if (LatencyManager != null)
			{
				adapter = ApplyOwnInner(new LatencyMessageAdapter(adapter) { LatencyManager = LatencyManager.Clone() });
			}

			if (SlippageManager != null)
			{
				adapter = ApplyOwnInner(new SlippageMessageAdapter(adapter) { SlippageManager = SlippageManager.Clone() });
			}

			if (adapter.IsNativeIdentifiers)
			{
				adapter = ApplyOwnInner(new SecurityNativeIdMessageAdapter(adapter, NativeIdStorage));
			}

			if (SecurityMappingStorage != null)
			{
				adapter = ApplyOwnInner(new SecurityMappingMessageAdapter(adapter, SecurityMappingStorage));
			}

			if (PnLManager != null && !adapter.IsSupportExecutionsPnL)
			{
				adapter = ApplyOwnInner(new PnLMessageAdapter(adapter) { PnLManager = PnLManager.Clone() });
			}

			if (CommissionManager != null)
			{
				adapter = ApplyOwnInner(new CommissionMessageAdapter(adapter) { CommissionManager = CommissionManager.Clone() });
			}

			if (SupportPartialDownload)
			{
				adapter = ApplyOwnInner(new PartialDownloadMessageAdapter(adapter));
			}

			if (adapter.IsSupportSubscriptions)
			{
				adapter = ApplyOwnInner(new SubscriptionMessageAdapter(adapter)
				{
					IsRestoreSubscriptionOnErrorReconnect = IsRestoreSubscriptionOnErrorReconnect,
				});
			}

			if (adapter.IsFullCandlesOnly)
			{
				adapter = ApplyOwnInner(new CandleHolderMessageAdapter(adapter));
			}

			if (SupportLookupTracking)
			{
				adapter = ApplyOwnInner(new LookupTrackingMessageAdapter(adapter));
			}

			if (StorageRegistry != null)
			{
				adapter = ApplyOwnInner(new StorageMessageAdapter(adapter, StorageRegistry, SnapshotRegistry, CandleBuilderProvider)
				{
					FilterSubscription = StorageFilterSubscription,
					Drive = StorageDrive,
					DaysLoad = StorageDaysLoad,
					Format = StorageFormat,
					Mode = StorageMode,
				});
			}

			if (SupportBuildingFromOrderLog)
			{
				adapter = ApplyOwnInner(new OrderLogMessageAdapter(adapter));
			}

			if (adapter.IsSupportOrderBookIncrements)
			{
				adapter = ApplyOwnInner(new OrderBookInrementMessageAdapter(adapter));
			}

			if (SupportOrderBookTruncate)
			{
				adapter = ApplyOwnInner(new OrderBookTruncateMessageAdapter(adapter));
			}

			if (SupportCandlesCompression)
			{
				adapter = ApplyOwnInner(new CandleBuilderMessageAdapter(adapter, CandleBuilderProvider));
			}

			if (ExtendedInfoStorage != null && !adapter.SecurityExtendedFields.IsEmpty())
			{
				adapter = ApplyOwnInner(new ExtendedInfoStorageMessageAdapter(adapter, ExtendedInfoStorage));
			}

			return adapter;
		}

		private readonly Dictionary<IMessageAdapter, bool> _hearbeatFlags = new Dictionary<IMessageAdapter, bool>();

		private bool IsHeartbeatOn(IMessageAdapter adapter)
		{
			return _hearbeatFlags.TryGetValue2(adapter) ?? true;
		}

		/// <summary>
		/// Apply on/off heartbeat mode for the specified adapter.
		/// </summary>
		/// <param name="adapter">Adapter.</param>
		/// <param name="on">Is active.</param>
		public void ApplyHeartbeat(IMessageAdapter adapter, bool on)
		{
			_hearbeatFlags[adapter] = on;
		}

		private IMessageAdapter GetUnderlyingAdapter(IMessageAdapter adapter)
		{
			if (adapter == null)
				throw new ArgumentNullException(nameof(adapter));

			if (adapter is IMessageAdapterWrapper wrapper)
			{
				return wrapper is IRealTimeEmulationMarketDataAdapter || wrapper is IHistoryMessageAdapter
					? wrapper
					: GetUnderlyingAdapter(wrapper.InnerAdapter);
			}

			return adapter;
		}

		/// <inheritdoc />
		void IMessageChannel.SendInMessage(Message message)
		{
			OnSendInMessage(message);
		}

		private static Tuple<ConnectionStates, Exception> CreateState(ConnectionStates state, Exception error = null)
		{
			if (state == ConnectionStates.Failed && error == null)
				throw new ArgumentNullException(nameof(error));

			return Tuple.Create(state, error);
		}

		private long[] GetSubscribers(DataType dataType) => _subscription.SyncGet(c => c.Where(p => p.Value.Item3 == dataType).Select(p => p.Key).ToArray());

		/// <summary>
		/// Send message.
		/// </summary>
		/// <param name="message">Message.</param>
		protected virtual void OnSendInMessage(Message message)
		{
			try
			{
				InternalSendInMessage(message);
			}
			catch (Exception ex)
			{
				this.AddErrorLog(ex);

				message.HandleErrorResponse(ex, CurrentTime, SendOutMessage, GetSubscribers);

				SendOutError(ex);
			}
		}

		private void InternalSendInMessage(Message message)
		{
			if (message is ITransactionIdMessage transIdMsg && transIdMsg.TransactionId == 0)
				throw new ArgumentException(message.ToString());

			if (message.IsBack)
			{
				var adapter = message.Adapter;

				if (adapter == null)
					throw new InvalidOperationException();

				if (adapter == this)
				{
					message.UndoBack();
				}
				else
				{
					ProcessAdapterMessage(adapter, message);
					return;	
				}
			}

			switch (message.Type)
			{
				case MessageTypes.Reset:
					ProcessReset((ResetMessage)message, false);
					break;

				case MessageTypes.Connect:
				{
					ProcessReset(new ResetMessage(), true);

					_currState = ConnectionStates.Connecting;

					_adapterWrappers.AddRange(GetSortedAdapters().ToDictionary(a => a, a =>
					{
						var adapter = a;

						lock (_connectedResponseLock)
							_adapterStates.Add(adapter, CreateState(ConnectionStates.Connecting));

						adapter = CreateWrappers(adapter);

						adapter.NewOutMessage += m => OnInnerAdapterNewOutMessage(adapter, m);
						
						return adapter;
					}));
					
					if (Wrappers.Length == 0)
						throw new InvalidOperationException(LocalizedStrings.Str3650);

					Wrappers.ForEach(w =>
					{
						var u = GetUnderlyingAdapter(w);
						this.AddInfoLog("Connecting '{0}'.", u);

						w.SendInMessage(message);
					});

					break;
				}

				case MessageTypes.Disconnect:
				{
					IDictionary<IMessageAdapter, IMessageAdapter> adapters;

					lock (_connectedResponseLock)
					{
						_currState = ConnectionStates.Disconnecting;

						adapters = _adapterStates.ToArray().Where(p => _adapterWrappers.ContainsKey(p.Key) && (p.Value.Item1 == ConnectionStates.Connecting || p.Value.Item1 == ConnectionStates.Connected)).ToDictionary(p => _adapterWrappers[p.Key], p =>
						{
							var underlying = p.Key;
							_adapterStates[underlying] = CreateState(ConnectionStates.Disconnecting);
							return underlying;
						});
					}

					foreach (var a in adapters)
					{
						var wrapper = a.Key;
						var underlying = a.Value;

						this.AddInfoLog("Disconnecting '{0}'.", underlying);
						wrapper.SendInMessage(message);
					}

					break;
				}

				case MessageTypes.Portfolio:
				{
					ProcessPortfolioMessage((PortfolioMessage)message);
					break;
				}

				case MessageTypes.OrderRegister:
				{
					var ordMsg = (OrderMessage)message;
					ProcessPortfolioMessage(ordMsg.PortfolioName, ordMsg);
					break;
				}
				case MessageTypes.OrderReplace:
				case MessageTypes.OrderCancel:
				{
					var ordMsg = (OrderMessage)message;
					ProcessOrderMessage(ordMsg.TransactionId, ordMsg.OriginalTransactionId, ordMsg);
					break;
				}
				case MessageTypes.OrderPairReplace:
				{
					var ordMsg = (OrderPairReplaceMessage)message;
					var m1 = ordMsg.Message1;
					ProcessOrderMessage(m1.TransactionId, m1.OriginalTransactionId, ordMsg);
					break;
				}
				case MessageTypes.OrderGroupCancel:
				{
					var groupMsg = (OrderGroupCancelMessage)message;

					if (groupMsg.PortfolioName.IsEmpty())
						ProcessOtherMessage(message);
					else
						ProcessPortfolioMessage(groupMsg.PortfolioName, groupMsg);

					break;
				}

				case MessageTypes.MarketData:
				{
					ProcessMarketDataRequest((MarketDataMessage)message);
					break;
				}

				case MessageTypes.ChangePassword:
				{
					var adapter = GetSortedAdapters().FirstOrDefault(a => a.IsMessageSupported(MessageTypes.ChangePassword));

					if (adapter == null)
						throw new InvalidOperationException(LocalizedStrings.Str629Params.Put(message.Type));

					adapter.SendInMessage(message);
					break;
				}

				default:
				{
					ProcessOtherMessage(message);
					break;
				}
			}
		}

		/// <inheritdoc />
		public event Action<Message> NewOutMessage;

		private void ProcessAdapterMessage(IMessageAdapter adapter, Message message)
		{
			if (message is ISubscriptionMessage subscrMsg)
				SendRequest((ISubscriptionMessage)subscrMsg.Clone(), adapter);
			else
				adapter.SendInMessage(message);
		}

		private void ProcessOtherMessage(Message message)
		{
			if (message.Adapter != null)
			{
				message.Adapter.SendInMessage(message);
				return;
			}

			if (message.Type.IsLookup())
			{
				var subscrMsg = (ISubscriptionMessage)message;

				IMessageAdapter[] adapters;

				if (subscrMsg.IsSubscribe)
				{
					adapters = GetAdapters(message, out var isPended, out _);

					if (isPended)
						return;

					if (adapters.Length == 0)
					{
						SendOutMessage(subscrMsg.CreateResult());
						return;
					}

					_subscription.TryAdd(subscrMsg.TransactionId, Tuple.Create((ISubscriptionMessage)subscrMsg.Clone(), adapters, subscrMsg.ToDataType()));
				}
				else
					adapters = null;

				foreach (var pair in ToChild(subscrMsg, adapters))
					SendRequest(pair.Key, pair.Value);
			}
			else
			{
				var adapters = GetAdapters(message, out var isPended, out _);

				if (isPended)
					return;

				if (adapters.Length == 0)
					return;

				adapters.ForEach(a => a.SendInMessage(message));
			}
		}

		private IMessageAdapter[] GetAdapters(Message message, out bool isPended, out bool skipSupportedMessages)
		{
			isPended = false;
			skipSupportedMessages = false;

			IMessageAdapter[] adapters = null;

			var adapter = message.Adapter;

			if (adapter != null)
				adapter = GetUnderlyingAdapter(adapter);

			if (adapter == null && message is MarketDataMessage mdMsg && mdMsg.DataType.IsSecurityRequired())
				adapter = _securityAdapters.TryGetValue(Tuple.Create(mdMsg.SecurityId, (MarketDataTypes?)mdMsg.DataType)) ?? _securityAdapters.TryGetValue(Tuple.Create(mdMsg.SecurityId, (MarketDataTypes?)null));

			if (adapter != null)
			{
				adapter = _adapterWrappers.TryGetValue(adapter);

				if (adapter != null)
				{
					adapters = new[] { adapter };
					skipSupportedMessages = true;
				}
			}

			lock (_connectedResponseLock)
			{
				if (adapters == null)
					adapters = _messageTypeAdapters.TryGetValue(message.Type)?.Cache;

				if (adapters != null)
				{
					if (message.Type == MessageTypes.MarketData)
					{
						var mdMsg1 = (MarketDataMessage)message;
						var set = _nonSupportedAdapters.TryGetValue(mdMsg1.TransactionId);

						if (set != null)
						{
							adapters = adapters.Where(a => !set.Contains(GetUnderlyingAdapter(a))).ToArray();
						}
						else if (mdMsg1.DataType == MarketDataTypes.News && mdMsg1.SecurityId == default)
						{
							adapters = adapters.Where(a => !a.IsSecurityNewsOnly).ToArray();
						}

						if (adapters.Length == 0)
							adapters = null;
					}
					else if (message.Type == MessageTypes.SecurityLookup)
					{
						var isAll = ((SecurityLookupMessage)message).IsLookupAll();

						if (isAll)
							adapters = adapters.Where(a => a.IsSupportSecuritiesLookupAll()).ToArray();
					}
				}

				if (adapters == null)
				{
					if (HasPendingAdapters() || _adapterStates.Count == 0 || _adapterStates.All(p => p.Value.Item1 == ConnectionStates.Disconnected || p.Value.Item1 == ConnectionStates.Failed))
					{
						isPended = true;
						_pendingMessages.Enqueue(message.Clone());
						return ArrayHelper.Empty<IMessageAdapter>();
					}
				}
			}

			if (adapters == null)
			{
				adapters = ArrayHelper.Empty<IMessageAdapter>();
			}

			if (adapters.Length == 0)
			{
				this.AddInfoLog(LocalizedStrings.Str629Params.Put(message));
				//throw new InvalidOperationException(LocalizedStrings.Str629Params.Put(message));
			}

			return adapters;
		}

		private bool HasPendingAdapters()
			=> _adapterStates.Any(p => p.Value.Item1 == ConnectionStates.Connecting);

		private IMessageAdapter[] GetSubscriptionAdapters(MarketDataMessage mdMsg, out bool isPended)
		{
			var adapters = GetAdapters(mdMsg, out isPended, out var skipSupportedMessages).Where(a =>
			{
				if (skipSupportedMessages)
					return true;

				if (mdMsg.DataType != MarketDataTypes.CandleTimeFrame)
				{
					var isCandles = mdMsg.DataType.IsCandleDataType();

					if (a.IsMarketDataTypeSupported(mdMsg.DataType) && (!isCandles || a.IsCandlesSupported(mdMsg)))
						return true;
					else
					{
						switch (mdMsg.DataType)
						{
							case MarketDataTypes.Level1:
							case MarketDataTypes.OrderLog:
							case MarketDataTypes.News:
							case MarketDataTypes.Board:
								return false;
							case MarketDataTypes.MarketDepth:
							{
								if (mdMsg.BuildMode == MarketDataBuildModes.Load)
									return false;

								switch (mdMsg.BuildFrom)
								{
									case MarketDataTypes.Level1:
										return a.IsMarketDataTypeSupported(MarketDataTypes.Level1);
									case MarketDataTypes.OrderLog:
										return a.IsMarketDataTypeSupported(MarketDataTypes.OrderLog);
									case null:
									{
										if (a.IsMarketDataTypeSupported(MarketDataTypes.OrderLog))
											mdMsg.BuildFrom = MarketDataTypes.OrderLog;
										else if (a.IsMarketDataTypeSupported(MarketDataTypes.Level1))
											mdMsg.BuildFrom = MarketDataTypes.Level1;
										else
											return false;

										return true;
									}
								}

								return false;
							}
							case MarketDataTypes.Trades:
								return a.IsMarketDataTypeSupported(MarketDataTypes.OrderLog);
							default:
							{
								if (isCandles && a.TryGetCandlesBuildFrom(mdMsg, CandleBuilderProvider) != null)
									return true;

								return false;
								//throw new ArgumentOutOfRangeException(mdMsg.DataType.ToString());
							}
						}
					}
				}

				var original = mdMsg.GetTimeFrame();
				var timeFrames = a.GetTimeFrames(mdMsg.SecurityId, mdMsg.From, mdMsg.To).ToArray();

				if (timeFrames.Contains(original) || a.CheckTimeFrameByRequest)
					return true;

				if (mdMsg.AllowBuildFromSmallerTimeFrame)
				{
					var smaller = timeFrames
					              .FilterSmallerTimeFrames(original)
					              .OrderByDescending()
					              .FirstOr();

					if (smaller != null)
						return true;
				}

				return a.TryGetCandlesBuildFrom(mdMsg, CandleBuilderProvider) != null;
			}).ToArray();

			//if (!isPended && adapters.Length == 0)
			//	throw new InvalidOperationException(LocalizedStrings.Str629Params.Put(mdMsg));

			return adapters;
		}

		private IDictionary<ISubscriptionMessage, IMessageAdapter> ToChild(ISubscriptionMessage subscrMsg, IMessageAdapter[] adapters)
		{
			// sending to inner adapters unique child requests

			var child = new Dictionary<ISubscriptionMessage, IMessageAdapter>();

			if (subscrMsg.IsSubscribe)
			{
				foreach (var adapter in adapters)
				{
					var clone = (ISubscriptionMessage)subscrMsg.Clone();
					clone.TransactionId = adapter.TransactionIdGenerator.GetNextId();

					child.Add(clone, adapter);

					_parentChildMap.AddMapping(clone.TransactionId, subscrMsg, adapter);
				}
			}
			else
			{
				var originTransId = subscrMsg.OriginalTransactionId;

				foreach (var pair in _parentChildMap.GetChild(originTransId))
				{
					var adapter = pair.Value;

					var clone = (ISubscriptionMessage)subscrMsg.Clone();
					clone.TransactionId = adapter.TransactionIdGenerator.GetNextId();
					clone.OriginalTransactionId = pair.Key;

					child.Add(clone, adapter);

					_parentChildMap.AddMapping(clone.TransactionId, subscrMsg, adapter);
				}
			}

			return child;
		}

		private void SendRequest(ISubscriptionMessage subscrMsg, IMessageAdapter adapter)
		{
			// if the message was looped back via IsBack=true
			_requestsById.TryAdd(subscrMsg.TransactionId, Tuple.Create(subscrMsg, GetUnderlyingAdapter(adapter)));
			this.AddInfoLog("Send to {0}: {1}", adapter, subscrMsg);
			adapter.SendInMessage((Message)subscrMsg);
		}

		private void ProcessMarketDataRequest(MarketDataMessage mdMsg)
		{
			IMessageAdapter[] GetAdapters()
			{
				if (!mdMsg.IsSubscribe)
					return null;

				var adapters = GetSubscriptionAdapters(mdMsg, out var isPended);

				if (isPended)
					return null;

				if (adapters.Length == 0)
				{
					SendOutMessage(mdMsg.TransactionId.CreateNotSupported());
					return null;
				}

				return adapters;
			}

			switch (mdMsg.DataType)
			{
				case MarketDataTypes.News:
				case MarketDataTypes.Board:
				{
					var adapters = GetAdapters();

					if (mdMsg.IsSubscribe)
					{
						if (adapters == null)
							return;

						_subscription.TryAdd(mdMsg.TransactionId, Tuple.Create((ISubscriptionMessage)mdMsg.Clone(), adapters, mdMsg.ToDataType()));
					}
					
					foreach (var pair in ToChild(mdMsg, adapters))
						SendRequest(pair.Key, pair.Value);

					break;
				}

				default:
				{
					IMessageAdapter adapter;

					if (mdMsg.IsSubscribe)
					{
						adapter = GetAdapters()?.First();

						if (adapter == null)
							return;

						mdMsg = (MarketDataMessage)mdMsg.Clone();
						_subscription.TryAdd(mdMsg.TransactionId, Tuple.Create((ISubscriptionMessage)mdMsg.Clone(), new[] { adapter }, mdMsg.ToDataType()));
					}
					else
					{
						var originTransId = mdMsg.OriginalTransactionId;

						if (!_subscription.TryGetValue(originTransId, out var tuple))
						{
							this.AddInfoLog("Unsubscribe not found: {0}/{1}", originTransId, mdMsg);
							break;
						}
						
						adapter = tuple.Item2.First();

						mdMsg = (MarketDataMessage)mdMsg.Clone();
					}

					SendRequest(mdMsg, adapter);
					break;
				}
			}
		}

		private void ProcessPortfolioMessage(string portfolioName, Message message)
		{
			var adapter = message.Adapter;

			if (adapter == null)
			{
				adapter = GetAdapter(portfolioName, message);

				if (adapter == null)
				{
					this.AddDebugLog("No adapter for {0}", message);
					return;
				}
			}

			if (message is OrderRegisterMessage regMsg)
				_orderAdapters.TryAdd(regMsg.TransactionId, adapter);
				
			adapter.SendInMessage(message);
		}

		private void ProcessOrderMessage(long transId, long originId, Message message)
		{
			if (!_orderAdapters.TryGetValue(originId, out var adapter))
			{
				this.AddErrorLog(LocalizedStrings.UnknownTransactionId, originId);

				SendOutMessage(new ExecutionMessage
				{
					ExecutionType = ExecutionTypes.Transaction,
					HasOrderInfo = true,
					OriginalTransactionId = transId,
					Error = new InvalidOperationException(LocalizedStrings.UnknownTransactionId.Put(originId)),
				});

				return;
			}

			adapter.SendInMessage(message);
		}

		private void ProcessPortfolioMessage(PortfolioMessage message)
		{
			if (message.IsSubscribe)
			{
				var adapter = GetAdapter(message.PortfolioName, message);

				if (adapter == null)
				{
					this.AddDebugLog("No adapter for {0}", message);

					SendOutMessage(message.TransactionId.CreateSubscriptionResponse(new InvalidOperationException(LocalizedStrings.Str629Params.Put(message))));
				}
				else
				{
					_portfolioAdapters.TryAdd(message.PortfolioName, GetUnderlyingAdapter(adapter));
					SendRequest((PortfolioMessage)message.Clone(), adapter);
				}
			}
			else
			{
				var originTransId = message.OriginalTransactionId;

				IMessageAdapter adapter;

				if (originTransId == 0)
					adapter = _portfolioAdapters.TryGetValue(message.PortfolioName);
				else if (_requestsById.TryGetValue(originTransId, out var tuple))
				{
					adapter = tuple.Item2;

					var transId = message.TransactionId;
					message = (PortfolioMessage)message.Clone();
					((PortfolioMessage)tuple.Item1).CopyTo(message);
					message.IsSubscribe = false;
					message.TransactionId = transId;
				}
				else
					adapter = null;

				if (adapter == null)
					this.AddDebugLog("No adapter for {0}", message);
				else
					SendRequest(message, adapter);
			}
		}

		private IMessageAdapter GetAdapter(string portfolioName, Message message)
		{
			if (portfolioName.IsEmpty())
				throw new ArgumentNullException(nameof(portfolioName));

			if (message == null)
				throw new ArgumentNullException(nameof(message));

			if (!_portfolioAdapters.TryGetValue(portfolioName, out var adapter))
			{
				return GetAdapters(message, out _, out _).FirstOrDefault();
			}
			else
			{
				var a = _adapterWrappers.TryGetValue(adapter);

				if (a == null)
					throw new InvalidOperationException(LocalizedStrings.ConnectionIsNotConnected.Put(adapter));
				
				return a;
			}
		}

		/// <summary>
		/// The embedded adapter event <see cref="IMessageChannel.NewOutMessage"/> handler.
		/// </summary>
		/// <param name="innerAdapter">The embedded adapter.</param>
		/// <param name="message">Message.</param>
		protected virtual void OnInnerAdapterNewOutMessage(IMessageAdapter innerAdapter, Message message)
		{
			List<Message> extra = null;

			if (!message.IsBack)
			{
				if (message.Adapter == null)
					message.Adapter = innerAdapter;

				switch (message.Type)
				{
					case MessageTypes.Connect:
						extra = new List<Message>();
						ProcessConnectMessage(innerAdapter, (ConnectMessage)message, extra);
						break;

					case MessageTypes.Disconnect:
						extra = new List<Message>();
						ProcessDisconnectMessage(innerAdapter, (DisconnectMessage)message, extra);
						break;

					case MessageTypes.SubscriptionResponse:
						message = ProcessSubscriptionResponse(innerAdapter, (SubscriptionResponseMessage)message);
						break;

					case MessageTypes.SubscriptionFinished:
						message = ProcessSubscriptionFinished((SubscriptionFinishedMessage)message);
						break;

					case MessageTypes.SubscriptionOnline:
						message = ProcessSubscriptionOnline((SubscriptionOnlineMessage)message);
						break;

					case MessageTypes.Portfolio:
					//case MessageTypes.PortfolioChange:
					case MessageTypes.PositionChange:
					{
						var pfMsg = (IPortfolioNameMessage)message;
						ApplyParentLookupId((ISubscriptionIdMessage)message);
						PortfolioAdapterProvider.SetAdapter(pfMsg.PortfolioName, GetUnderlyingAdapter(innerAdapter).Id);
						break;
					}

					case MessageTypes.Security:
						var secMsg = (SecurityMessage)message;
						ApplyParentLookupId(secMsg);
						SecurityAdapterProvider.SetAdapter(secMsg.SecurityId, null, GetUnderlyingAdapter(innerAdapter).Id);
						break;

					case MessageTypes.Board:
					case MessageTypes.BoardState:
						ApplyParentLookupId((ISubscriptionIdMessage)message);
						break;

					case MessageTypes.Execution:
						var execMsg = (ExecutionMessage)message;

						if (execMsg.ExecutionType != ExecutionTypes.Transaction)
							break;

						ApplyParentLookupId(execMsg);

						if (execMsg.TransactionId != default)
						{
							if (execMsg.HasOrderInfo)
								_orderAdapters.TryAdd(execMsg.TransactionId, innerAdapter);
						}

						break;
				}
			}

			if (message != null)
				SendOutMessage(message);

			if (extra != null)
			{
				foreach (var m in extra)
					SendOutMessage(m);	
			}
		}

		private void ApplyParentLookupId(ISubscriptionIdMessage msg)
		{
			if (msg == null)
				throw new ArgumentNullException(nameof(msg));

			var originIds = msg.GetSubscriptionIds();
			var ids = originIds;
			var changed = false;

			for (var i = 0; i < ids.Length; i++)
			{
				var parentId = _parentChildMap.TryGetParent(ids[i]);

				if (parentId == null)
					continue;

				if (!changed)
				{
					ids = originIds.ToArray();
					changed = true;
				}

				ids[i] = parentId.Value;
			}

			if (changed)
				msg.SetSubscriptionIds(ids);
		}

		private void SendOutError(Exception error)
		{
			SendOutMessage(error.ToErrorMessage());
		}

		private void SendOutMessage(Message message)
		{
			OnSendOutMessage(message);
		}

		/// <summary>
		/// Send outgoing message and raise <see cref="NewOutMessage"/> event.
		/// </summary>
		/// <param name="message">Message.</param>
		protected virtual void OnSendOutMessage(Message message)
		{
			if (message.Adapter == null)
				message.Adapter = this;

			NewOutMessage?.Invoke(message);
		}

		private void ProcessConnectMessage(IMessageAdapter innerAdapter, ConnectMessage message, List<Message> extra)
		{
			var underlyingAdapter = GetUnderlyingAdapter(innerAdapter);
			var wrapper = _adapterWrappers[underlyingAdapter];

			var error = message.Error;

			if (error != null)
				this.AddErrorLog(LocalizedStrings.Str625Params, underlyingAdapter, error);
			else
				this.AddInfoLog("Connected to '{0}'.", underlyingAdapter);

			Message[] notSupportedMsgs = null;

			lock (_connectedResponseLock)
			{
				if (error != null)
				{
					if (!HasPendingAdapters())
					{
						notSupportedMsgs = _pendingMessages.ToArray();
						_pendingMessages.Clear();
					}
				}
				else
				{
					foreach (var supportedMessage in innerAdapter.SupportedInMessages)
					{
						_messageTypeAdapters.SafeAdd(supportedMessage).Add(wrapper);
					}

					extra.AddRange(_pendingMessages.Select(m => m.LoopBack(this)));

					_pendingMessages.Clear();
				}

				UpdateAdapterState(underlyingAdapter, true, error, extra);
			}

			if (notSupportedMsgs != null)
			{
				foreach (var notSupportedMsg in notSupportedMsgs)
				{
					SendOutError(new InvalidOperationException(LocalizedStrings.Str629Params.Put(notSupportedMsg.Type)));
				}
			}

			message.Adapter = underlyingAdapter;
		}

		private void ProcessDisconnectMessage(IMessageAdapter innerAdapter, DisconnectMessage message, List<Message> extra)
		{
			var underlyingAdapter = GetUnderlyingAdapter(innerAdapter);
			var wrapper = _adapterWrappers[underlyingAdapter];

			var error = message.Error;

			if (error == null)
				this.AddInfoLog("Disconnected from '{0}'.", underlyingAdapter);
			else
				this.AddErrorLog(LocalizedStrings.Str627Params, underlyingAdapter, error);

			lock (_connectedResponseLock)
			{
				foreach (var supportedMessage in innerAdapter.SupportedInMessages)
				{
					var list = _messageTypeAdapters.TryGetValue(supportedMessage);

					if (list == null)
						continue;

					list.Remove(wrapper);

					if (list.Count == 0)
						_messageTypeAdapters.Remove(supportedMessage);
				}

				UpdateAdapterState(underlyingAdapter, false, error, extra);
			}

			message.Adapter = underlyingAdapter;
		}

		private void UpdateAdapterState(IMessageAdapter adapter, bool isConnect, Exception error, List<Message> extra)
		{
			if (isConnect)
			{
				void CreateConnectedMsg(ConnectionStates newState, Exception e = null)
				{
					extra.Add(new ConnectMessage { Error = e });
					_currState = newState;
				}

				if (error == null)
				{
					_adapterStates[adapter] = CreateState(ConnectionStates.Connected);

					if (_currState == ConnectionStates.Connecting)
					{
						if (ConnectDisconnectEventOnFirstAdapter)
						{
							// raise Connected event only one time for the first adapter
							CreateConnectedMsg(ConnectionStates.Connected);
						}
						else
						{
							var noPending = _adapterStates.All(v => v.Value.Item1 != ConnectionStates.Connecting);

							if (noPending)
								CreateConnectedMsg(ConnectionStates.Connected);
						}
					}
				}
				else
				{
					_adapterStates[adapter] = CreateState(ConnectionStates.Failed, error);

					if (_currState == ConnectionStates.Connecting)
					{
						var allFailed = _adapterStates.All(v => v.Value.Item1 == ConnectionStates.Failed);

						if (allFailed)
							CreateConnectedMsg(ConnectionStates.Failed, new AggregateException(_adapterStates.Select(v => v.Value.Item2).Where(e => e != null).ToArray()));
					}
				}
			}
			else
			{
				if (error == null)
					_adapterStates[adapter] = CreateState(ConnectionStates.Disconnected);
				else
					_adapterStates[adapter] = CreateState(ConnectionStates.Failed, error);

				var noPending = _adapterStates.All(v => v.Value.Item1 == ConnectionStates.Disconnected || v.Value.Item1 == ConnectionStates.Failed);

				if (noPending)
				{
					extra.Add(new DisconnectMessage());
					_currState = ConnectionStates.Disconnected;
				}
			}
		}

		private SubscriptionOnlineMessage ProcessSubscriptionOnline(SubscriptionOnlineMessage message)
		{
			var originalTransactionId = message.OriginalTransactionId;

			var parentId = _parentChildMap.ProcessChildOnline(originalTransactionId, out var needParentResponse);

			if (parentId == null)
				return message;

			if (!needParentResponse)
				return null;

			return new SubscriptionOnlineMessage { OriginalTransactionId = parentId.Value };
		}

		private SubscriptionFinishedMessage ProcessSubscriptionFinished(SubscriptionFinishedMessage message)
		{
			var originalTransactionId = message.OriginalTransactionId;

			_requestsById.Remove(originalTransactionId);

			var parentId = _parentChildMap.ProcessChildFinish(originalTransactionId, out var needParentResponse);

			if (parentId == null)
			{
				_subscription.Remove(originalTransactionId);
				return message;
			}

			if (!needParentResponse)
				return null;

			_subscription.Remove(parentId.Value);
			return new SubscriptionFinishedMessage { OriginalTransactionId = parentId.Value };
		}

		private Message ProcessSubscriptionResponse(IMessageAdapter adapter, SubscriptionResponseMessage message)
		{
			var originalTransactionId = message.OriginalTransactionId;

			if (!_requestsById.TryGetValue(originalTransactionId, out var tuple))
				return message;

			var isOk = message.IsOk();
			var originMsg = tuple.Item1;

			if (!isOk)
			{
				this.AddWarningLog("Subscription Error out: {0}", message);
				_subscription.Remove(originalTransactionId);
				_requestsById.Remove(originalTransactionId);
			}
			else if (!originMsg.IsSubscribe)
			{
				// remove subscribe and unsubscribe requests
				_requestsById.Remove(originMsg.OriginalTransactionId);
				_requestsById.Remove(originalTransactionId);
			}

			var parentId = _parentChildMap.ProcessChildResponse(originalTransactionId, isOk, out var needParentResponse, out var allError);

			if (parentId != null)
			{
				if (allError)
					_subscription.Remove(parentId.Value);

				return needParentResponse
					? parentId.Value.CreateSubscriptionResponse(allError ? new InvalidOperationException(LocalizedStrings.Str629Params.Put(originMsg)) : null)
					: null;
			}
			else
			{
				if (!originMsg.IsSubscribe)
					_subscription.Remove(originMsg.OriginalTransactionId);
			}

			if (message.IsNotSupported() && originMsg is MarketDataMessage mdMsg)
			{
				lock (_connectedResponseLock)
				{
					// try loopback only subscribe messages
					if (mdMsg.IsSubscribe)
					{
						var set = _nonSupportedAdapters.SafeAdd(originalTransactionId, k => new HashSet<IMessageAdapter>());
						set.Add(GetUnderlyingAdapter(adapter));

						mdMsg.LoopBack(this);
					}
				}

				return mdMsg;
			}
			
			return message;
		}

		private void SecurityAdapterProviderOnChanged(Tuple<SecurityId, MarketDataTypes?> key, Guid adapterId, bool changeType)
		{
			if (changeType)
			{
				var adapter = InnerAdapters.SyncGet(c => c.FindById(adapterId));

				if (adapter == null)
					_securityAdapters.Remove(key);
				else
					_securityAdapters[key] = adapter;
			}
			else
				_securityAdapters.Remove(key);
		}

		private void PortfolioAdapterProviderOnChanged(string key, Guid adapterId, bool changeType)
		{
			if (changeType)
			{
				var adapter = InnerAdapters.SyncGet(c => c.FindById(adapterId));

				if (adapter == null)
					_portfolioAdapters.Remove(key);
				else
					_portfolioAdapters[key] = adapter;
			}
			else
				_portfolioAdapters.Remove(key);
		}

		/// <inheritdoc />
		public override void Save(SettingsStorage storage)
		{
			lock (InnerAdapters.SyncRoot)
			{
				storage.SetValue(nameof(InnerAdapters), InnerAdapters.Select(a =>
				{
					var s = new SettingsStorage();

					s.SetValue("AdapterType", a.GetType().GetTypeName(false));
					s.SetValue("AdapterSettings", a.Save());
					s.SetValue("Priority", InnerAdapters[a]);

					return s;
				}).ToArray());
			}

			base.Save(storage);
		}

		/// <inheritdoc />
		public override void Load(SettingsStorage storage)
		{
			lock (InnerAdapters.SyncRoot)
			{
				InnerAdapters.Clear();

				var adapters = new Dictionary<Guid, IMessageAdapter>();

				foreach (var s in storage.GetValue<IEnumerable<SettingsStorage>>(nameof(InnerAdapters)))
				{
					try
					{
						var adapter = s.GetValue<Type>("AdapterType").CreateAdapter(TransactionIdGenerator);
						adapter.Load(s.GetValue<SettingsStorage>("AdapterSettings"));
						InnerAdapters[adapter] = s.GetValue<int>("Priority");

						adapters.Add(adapter.Id, adapter);
					}
					catch (Exception e)
					{
						e.LogError();
					}
				}

				_securityAdapters.Clear();

				foreach (var pair in SecurityAdapterProvider.Adapters)
				{
					if (!adapters.TryGetValue(pair.Value, out var adapter))
						continue;

					_securityAdapters.Add(pair.Key, adapter);
				}

				_portfolioAdapters.Clear();

				foreach (var pair in PortfolioAdapterProvider.Adapters)
				{
					if (!adapters.TryGetValue(pair.Value, out var adapter))
						continue;

					_portfolioAdapters.Add(pair.Key, adapter);
				}
			}

			base.Load(storage);
		}

		/// <summary>
		/// To release allocated resources.
		/// </summary>
		protected override void DisposeManaged()
		{
			SecurityAdapterProvider.Changed -= SecurityAdapterProviderOnChanged;
			PortfolioAdapterProvider.Changed -= PortfolioAdapterProviderOnChanged;

			Wrappers.ForEach(a => a.Parent = null);

			base.DisposeManaged();
		}

		/// <summary>
		/// Create a copy of <see cref="BasketMessageAdapter"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public IMessageChannel Clone()
		{
			var clone = new BasketMessageAdapter(TransactionIdGenerator, SecurityAdapterProvider, PortfolioAdapterProvider, CandleBuilderProvider, StorageRegistry, SnapshotRegistry)
			{
				ExtendedInfoStorage = ExtendedInfoStorage,
				SupportCandlesCompression = SupportCandlesCompression,
				SuppressReconnectingErrors = SuppressReconnectingErrors,
				IsRestoreSubscriptionOnErrorReconnect = IsRestoreSubscriptionOnErrorReconnect,
				SupportBuildingFromOrderLog = SupportBuildingFromOrderLog,
				SupportOrderBookTruncate = SupportOrderBookTruncate,
				SupportOffline = SupportOffline,
				IgnoreExtraAdapters = IgnoreExtraAdapters,
				NativeIdStorage = NativeIdStorage,
				StorageDaysLoad = StorageDaysLoad,
				StorageMode = StorageMode,
				StorageFormat = StorageFormat,
				StorageDrive = StorageDrive,
				StorageFilterSubscription = StorageFilterSubscription,
				ConnectDisconnectEventOnFirstAdapter = ConnectDisconnectEventOnFirstAdapter,
				UseSeparatedChannels = UseSeparatedChannels,
			};

			clone.Load(this.Save());

			return clone;
		}

		object ICloneable.Clone()
		{
			return Clone();
		}

		bool IMessageChannel.IsOpened => true;

		void IMessageChannel.Open()
		{
		}

		void IMessageChannel.Close()
		{
		}

		event Action IMessageChannel.StateChanged
		{
			add { }
			remove { }
		}
	}
}