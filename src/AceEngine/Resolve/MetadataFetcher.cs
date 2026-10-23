using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.P2P;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Resolve;

/// <summary>What is requested from the peers.</summary>
public enum MetadataMode
{
	/// <summary>Complete transport file of an Ace engine ({msg_type:0, version:1, full:1}), by content_id.</summary>
	AceFull,
	/// <summary>'info' of a BitTorrent infohash through ut_metadata (BEP9), verified with SHA-1 against the infohash.</summary>
	Bep9,
	/// <summary>
	/// Infohash of a link (Ace search result, magnet): each peer decides. If it announces 'metadata_size' (VOD torrent) the 'info' is requested through BEP9;
	/// if it is an Ace engine without 'metadata_size' (a live stream: there is no 'info') the complete transport ('full') is requested. In both cases the result is
	/// verified against the requested infohash (SHA-1 of the 'info', or the infohash of the live descriptor = SHA-1 of its hash_fields).
	/// </summary>
	InfoHash
}

/// <summary>
/// Obtains the transport file (Ace "full" mode) or the 'info' of an infohash (BEP9) from the peers: DHT + trackers + fixed peers, up to
/// MaxConnections connections. Equivalent to the original's MiniBitTorrent. The content_id is not the SHA-1 of the file, so the
/// file is accepted by request_hash with a quorum of distinct NETWORKS (/24 in IPv4, /48 in IPv6) and, if there are two different files, by majority.
/// </summary>
public sealed class MetadataFetcher
{
	private sealed class Cand
	{
		public IPEndPoint EndPoint;

		public int Attempts;

		public bool InUse;

		public string Source;
	}

	private sealed class Job
	{
		private sealed class Weak
		{
			public byte[] Data;

			public readonly HashSet<string> Peers = new HashSet<string>();

			/// <summary>Networks of the peers that delivered it: the quorum counts networks, not peers.</summary>
			public readonly HashSet<string> Networks = new HashSet<string>();

			public long FirstMs;
		}

		private readonly MetadataFetcher _f;

		private readonly byte[] _hash;

		private readonly string _hashHex;

		private readonly MetadataMode _mode;

		private readonly CancellationTokenSource _cts;

		private readonly ResolverOptions _ro;

		private readonly byte[] _peerId;

		private readonly object _gate = new object();

		private readonly Dictionary<string, Cand> _cands = new Dictionary<string, Cand>();

		private readonly TaskCompletionSource<byte[]> _result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

		private int _active;

		private bool _done;

		private byte[] _buf;

		private bool[] _got;

		private long[] _reqAt;

		private int _size;

		public readonly List<string> Trackers = new List<string>();

		public ICollection<IPEndPoint> GoodPeers;

		public FetchReport Report;

		/// <summary>Budget and statistics of this resolution (input and output).</summary>
		public ResolveProfile Profile = new ResolveProfile();

		private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

		private ResolveStats St => Profile.Stats;

		private int MaxConn => (Profile.MaxConnections > 0) ? Profile.MaxConnections : _ro.MaxConnections;

		private TimeSpan ConnectTimeout => (Profile.ConnectTimeout > TimeSpan.Zero) ? Profile.ConnectTimeout : (_ro.ConnectTimeout > TimeSpan.Zero) ? _ro.ConnectTimeout : _f._opt.ConnectTimeout;

		private TimeSpan HandshakeTimeout => (Profile.HandshakeTimeout > TimeSpan.Zero) ? Profile.HandshakeTimeout : (_ro.HandshakeTimeout > TimeSpan.Zero) ? _ro.HandshakeTimeout : _f._opt.HandshakeTimeout;

		public void Finish()
		{
			lock (_gate)
			{
				St.Candidates = _cands.Count;
				int untried = 0;
				foreach (Cand c in _cands.Values)
				{
					if (c.Attempts == 0)
					{
						untried++;
					}
				}
				St.UntriedAtEnd = untried;
				St.ElapsedSeconds = _sw.Elapsed.TotalSeconds;
				St.BudgetConnections = MaxConn;
				St.ConnectTimeoutSeconds = ConnectTimeout.TotalSeconds;
				St.HandshakeTimeoutSeconds = HandshakeTimeout.TotalSeconds;
			}
		}

		/// <summary>Cap on candidates per search (DHT, trackers and PEX can return huge and hostile lists).</summary>
		private const int MaxCandidates = 3000;

		private readonly Dictionary<string, Weak> _weak = new Dictionary<string, Weak>();

		private int KnownSize
		{
			get
			{
				lock (_gate)
				{
					return _size;
				}
			}
		}

		private void NoteGoodPeer(IPEndPoint ep)
		{
			ICollection<IPEndPoint> goodPeers = GoodPeers;
			if (goodPeers == null)
			{
				return;
			}
			lock (goodPeers)
			{
				if (!goodPeers.Contains(ep))
				{
					goodPeers.Add(ep);
				}
			}
		}

		public Job(MetadataFetcher f, byte[] hash, MetadataMode mode, CancellationTokenSource cts)
		{
			_f = f;
			_hash = hash;
			_hashHex = Hex.ToHex(hash);
			_mode = mode;
			_cts = cts;
			_ro = f._opt.Resolver;
			_peerId = new byte[20];
			byte[] bytes = Encoding.ASCII.GetBytes(_ro.PeerIdPrefix ?? "-ST0100-");
			int num = Math.Min(bytes.Length, 20);
			Buffer.BlockCopy(bytes, 0, _peerId, 0, num);
			byte[] array = new byte[20 - num];
			using (RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create())
			{
				randomNumberGenerator.GetBytes(array);
			}
			Buffer.BlockCopy(array, 0, _peerId, num, array.Length);
		}

		public void AddCandidates(IEnumerable<IPEndPoint> eps, string source)
		{
			int num = 0;
			lock (_gate)
			{
				foreach (IPEndPoint ep in eps)
				{
					if (_cands.Count >= MaxCandidates)
					{
						break;
					}
					if (ep != null && ep.Port > 0 && !ep.Address.Equals(IPAddress.Any) && !ep.Address.Equals(IPAddress.None))
					{
						string key = ep.ToString();
						if (!_cands.ContainsKey(key))
						{
							_cands[key] = new Cand
							{
								EndPoint = ep,
								Source = source
							};
							num++;
						}
					}
				}
			}
			if (num > 0)
			{
				if (St.FirstCandidateSeconds < 0.0)
				{
					St.FirstCandidateSeconds = _sw.Elapsed.TotalSeconds;
				}
				AceLog.Debug("metadata", _hashHex.Substring(0, 8) + ": +" + num + " candidates (" + source + ")");
			}
		}

		public async Task<byte[]> RunAsync()
		{
			CancellationToken ct = _cts.Token;
			AceLog.Info("metadata", "searching for " + ((_mode == MetadataMode.AceFull) ? "complete transport (full)" : (_mode == MetadataMode.InfoHash) ? "transport (full) or BEP9 info, depending on the peer," : "info BEP9") + " of " + _hashHex);
			List<Task> list = new List<Task>();
			if (_f._opt.EnableDht && _f._dht != null)
			{
				list.Add(Task.Run(() => DhtLoopAsync()));
			}
			if (_f._opt.EnableTrackers)
			{
				foreach (string item in _ro.ExtraTrackers.Concat(Trackers).Distinct())
				{
					string url = item;
					list.Add(Task.Run(() => TrackerLoopAsync(url)));
				}
			}
			try
			{
				while (!_done && !ct.IsCancellationRequested)
				{
					List<Cand> list2 = new List<Cand>();
					lock (_gate)
					{
						foreach (Cand value in _cands.Values)
						{
							if (_active + list2.Count < MaxConn)
							{
								if (!value.InUse && value.Attempts < 2)
								{
									_f.NoteConnectionStart();
									value.InUse = true;
									value.Attempts++;
									list2.Add(value);
								}
								continue;
							}
							break;
						}
						_active += list2.Count;
						if (_active > St.PeakConnections)
						{
							St.PeakConnections = _active;
						}
					}
					foreach (Cand item2 in list2)
					{
						Cand cc = item2;
						_ = Task.Run(() => PeerAsync(cc));
					}
					CheckWeakFallback();
					Task task = Task.Delay(150, ct);
					await Task.WhenAny(new Task[2] { _result.Task, task }).ConfigureAwait(continueOnCapturedContext: false);
					if (_result.Task.IsCompleted)
					{
						break;
					}
				}
			}
			catch (OperationCanceledException)
			{
			}
			if (_result.Task.IsCompleted)
			{
				return await _result.Task.ConfigureAwait(continueOnCapturedContext: false);
			}
			ct.ThrowIfCancellationRequested();
			return null;
		}

		private void Complete(byte[] data)
		{
			lock (_gate)
			{
				if (_done)
				{
					return;
				}
				_done = true;
			}
			_result.TrySetResult(data);
			try
			{
				_cts.Cancel();
			}
			catch
			{
			}
		}

		private async Task DhtLoopAsync()
		{
			CancellationToken ct = _cts.Token;
			try
			{
				while (!_done && !ct.IsCancellationRequested)
				{
					int num = await _f._dht.GetPeersAsync(_hash, delegate(IList<IPEndPoint> ps)
					{
						AddCandidates(ps, "dht");
					}, _ro.DhtSearchTimeout, ct, St.Dht).ConfigureAwait(continueOnCapturedContext: false);
					AceLog.Info("metadata", "DHT " + _hashHex.Substring(0, 8) + ": " + num + " peers (nodes " + _f._dht.KnownNodes + ")");
					await Task.Delay(_ro.DhtRetryDelay, ct).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex2)
			{
				AceLog.Warn("metadata", "DHT: " + ex2.Message);
			}
		}

		private async Task TrackerLoopAsync(string url)
		{
			CancellationToken ct = _cts.Token;
			if (!TrackerClient.IsSupported(url))
			{
				return;
			}
			uint key = (uint)Rng.Next(1, int.MaxValue) & 0xFFFFFFu;
			try
			{
				while (!_done && !ct.IsCancellationRequested)
				{
					AnnounceRequest req = new AnnounceRequest
					{
						InfoHash = _hash,
						PeerId = _peerId,
						Port = 6881,
						Left = 1L,
						Event = "started",
						NumWant = 100
					};
					AnnounceResult announceResult = await TrackerClient.AnnounceAsync(_f._http, url, req, key, ct).ConfigureAwait(continueOnCapturedContext: false);
					if (announceResult.Failed)
					{
						AceLog.Info("metadata", "tracker " + url + ": " + announceResult.FailureReason);
					}
					else
					{
						AceLog.Info("metadata", "tracker " + url + ": " + announceResult.Peers.Count + " peers");
						AddCandidates(announceResult.Peers, "tracker");
					}
					await Task.Delay(TimeSpan.FromSeconds(30.0), ct).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex2)
			{
				AceLog.Debug("metadata", "tracker " + url + ": " + ex2.Message);
			}
		}

		private async Task PeerAsync(Cand cand)
		{
			string reason = "end";
			TcpClient tcp = null;
			System.Diagnostics.Stopwatch psw = System.Diagnostics.Stopwatch.StartNew();
			bool hsOk = false;
			double hsMs = 0.0;
			try
			{
				tcp = new TcpClient(AddressFamily.InterNetwork)
				{
					NoDelay = true
				};
				CancellationToken ct = _cts.Token;
				Task connect = tcp.ConnectAsync(cand.EndPoint.Address, cand.EndPoint.Port).Observed();
				if (await Task.WhenAny(new Task[2]
				{
					connect,
					Task.Delay(ConnectTimeout, ct)
				}).ConfigureAwait(continueOnCapturedContext: false) != connect)
				{
					reason = "connect timeout";
					return;
				}
				await connect.ConfigureAwait(continueOnCapturedContext: false);
				NetworkStream stream = tcp.GetStream();
				byte[] array = Wire.BuildHandshake(_f._opt.HandshakeReserved, _hash, _peerId);
				await stream.WriteAsync(array, 0, array.Length, ct).ConfigureAwait(continueOnCapturedContext: false);
				byte[] one = new byte[1];
				int plen;
				byte[] rest;
				using (CancellationTokenSource hcts = CancellationTokenSource.CreateLinkedTokenSource(new CancellationToken[1] { ct }))
				{
					hcts.CancelAfter(HandshakeTimeout);
					try
					{
						await ReadExactAsync(stream, one, 1, hcts.Token).ConfigureAwait(continueOnCapturedContext: false);
						plen = one[0];
						if (plen != 19 && plen != 17)
						{
							reason = "bad len " + plen;
							return;
						}
						rest = new byte[plen + 8 + 20 + 20];
						await ReadExactAsync(stream, rest, rest.Length, hcts.Token).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (OperationCanceledException) when (!ct.IsCancellationRequested)
					{
						reason = "handshake timeout";
						return;
					}
				}
				hsOk = true;
				hsMs = psw.Elapsed.TotalMilliseconds;
				string @string = Encoding.ASCII.GetString(rest, 0, plen);
				bool remoteAce = plen == 17 && @string == "AceStreamProtocol";
				if (!remoteAce && @string != "BitTorrent protocol")
				{
					reason = "protocol " + @string;
					return;
				}
				byte b = rest[plen + 5];
				if (!remoteAce && (b & 0x10) == 0)
				{
					reason = "no extensions (BEP10)";
					return;
				}
				for (int i = 0; i < 20; i++)
				{
					if (rest[plen + 8 + i] != _hash[i])
					{
						AceLog.Debug("metadata", cand.EndPoint?.ToString() + ": answers with another infohash " + Hex.ToHex(rest, plen + 8, 20));
						break;
					}
				}
				byte[] array2 = new byte[20];
				Buffer.BlockCopy(rest, plen + 8 + 20, array2, 0, 20);
				if (array2.SequenceEqual(_peerId))
				{
					reason = "connection to self";
					return;
				}
				BDict bDict = new BDict();
				bDict.Add("ut_pex", BValue.Int(1L));
				bDict.Add("ut_metadata", BValue.Int(2L));
				BDict bDict2 = new BDict();
				bDict2.Add("m", bDict);
				int knownSize = KnownSize;
				if (knownSize > 0)
				{
					bDict2.Add("metadata_size", BValue.Int(knownSize));
				}
				if (remoteAce)
				{
					bDict2.Add("pv", BValue.Int(2L));
					bDict2.Add("ct", BValue.Str("metadata"));
				}
				byte[] payload = BencodeWriter.Encode(bDict2);
				byte[] array3 = Wire.Extended(0, payload);
				await stream.WriteAsync(array3, 0, array3.Length, ct).ConfigureAwait(continueOnCapturedContext: false);
				reason = await MessageLoopAsync(cand, stream, remoteAce).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				reason = "cancelled";
			}
			catch (EndOfStreamException)
			{
				reason = "closed by peer";
			}
			catch (IOException ex4)
			{
				reason = "I/O: " + ((ex4.InnerException != null) ? ex4.InnerException.Message : ex4.Message);
			}
			catch (SocketException ex5)
			{
				reason = "socket " + ex5.SocketErrorCode;
			}
			catch (ObjectDisposedException)
			{
				reason = "closed";
			}
			catch (Exception ex7)
			{
				reason = ex7.GetType().Name + ": " + ex7.Message;
				AceLog.Warn("metadata", cand.EndPoint?.ToString() + ": " + reason);
			}
			finally
			{
				try
				{
					tcp?.Close();
				}
				catch
				{
				}
				lock (_gate)
				{
					_active--;
					cand.InUse = false;
				}
				_f.NoteConnectionEnd();
				St.NoteAttempt(reason, hsOk, hsMs, psw.Elapsed.TotalSeconds);
				AceLog.Debug("metadata", cand.EndPoint?.ToString() + " (" + cand.Source + "): " + reason);
			}
		}

		private async Task<string> MessageLoopAsync(Cand cand, NetworkStream stream, bool remoteAce)
		{
			int hisMetadataId = 0;
			bool requested = false;
			bool fullRequested = false;
			byte[] lenBuf = new byte[4];
			int maxLen = _ro.MaxFetchMessageBytes;
			while (!_done)
			{
				using CancellationTokenSource rcts = CancellationTokenSource.CreateLinkedTokenSource(new CancellationToken[1] { _cts.Token });
				rcts.CancelAfter(_ro.InactiveTimeout);
				try
				{
					await ReadExactAsync(stream, lenBuf, 4, rcts.Token).ConfigureAwait(continueOnCapturedContext: false);
					uint u = Wire.GetU32(lenBuf, 0);
					if (u == 0)
					{
						continue;
					}
					if (u > (uint)maxLen)
					{
						return "message too large " + u;
					}
					byte[] msg = await StreamUtil.ReadMessageAsync(stream, (int)u, rcts.Token).ConfigureAwait(continueOnCapturedContext: false);
					if (msg[0] != 20)
					{
						continue;
					}
					if (msg.Length < 2)
					{
						return "short EXTEND";
					}
					switch (msg[1])
					{
					case 0:
					{
						if (!ExtensionMessages.TryParseExtendedHandshake(msg, 2, msg.Length - 2, out var info))
						{
							return "invalid extended handshake";
						}
						int valueOrDefault = info.AceMetadataVersion.GetValueOrDefault();
						if (valueOrDefault > 0 || remoteAce)
						{
							NoteGoodPeer(cand.EndPoint);
						}
						hisMetadataId = info.UtMetadataId;
						string[] obj = new string[9]
						{
							cand.EndPoint?.ToString(),
							" ext handshake: v=",
							info.Client,
							" ut_metadata=",
							hisMetadataId.ToString(),
							" metadata_size=",
							null,
							null,
							null
						};
						int? metadataSize = info.MetadataSize;
						obj[6] = metadataSize.ToString();
						obj[7] = " ace_metadata_version=";
						obj[8] = valueOrDefault.ToString();
						AceLog.Debug("metadata", string.Concat(obj));
						if (hisMetadataId == 0)
						{
							if (info.UtPexId == 0)
							{
								return "unsupported peer";
							}
							break;
						}
						// InfoHash: an Ace peer without metadata_size is a live stream (there is no 'info'), so only the complete transport works
						bool noInfoSize = !info.MetadataSize.HasValue || info.MetadataSize.Value <= 0;
						if (_mode == MetadataMode.AceFull || (_mode == MetadataMode.InfoHash && noInfoSize && valueOrDefault > 0))
						{
							if (valueOrDefault <= 0)
							{
								return "the peer is not an Ace engine (no ace_metadata_version)";
							}
							byte[] array4 = Wire.Extended((byte)hisMetadataId, ExtensionMessages.BuildAceFullMetadataRequest());
							await stream.WriteAsync(array4, 0, array4.Length, _cts.Token).ConfigureAwait(continueOnCapturedContext: false);
							requested = true;
							fullRequested = true;
							continue;
						}
						if (!info.MetadataSize.HasValue || info.MetadataSize.Value <= 0 || info.MetadataSize.Value > 8 * 1024 * 1024)
						{
							return "no metadata_size";
						}
						if (!PrepareBep9(info.MetadataSize.Value))
						{
							return "metadata_size differs from other peers'";
						}
						int num = await RequestNextPieceAsync(stream, hisMetadataId).ConfigureAwait(continueOnCapturedContext: false);
						requested = true;
						if (num == -2)
						{
							return "done";
						}
						goto end_IL_0071;
					}
					case 1:
					{
						List<IPEndPoint> list = new List<IPEndPoint>();
						if (ExtensionMessages.TryParsePex(msg, 2, msg.Length - 2, list))
						{
							AddCandidates(list, "pex");
						}
						goto end_IL_0071;
					}
					case 2:
					{
						if (!ExtensionMessages.TryParseMetadataMessage(msg, 2, msg.Length - 2, out var msg2))
						{
							return "invalid ut_metadata";
						}
						if (msg2.MsgType == 0)
						{
							if (hisMetadataId != 0)
							{
								byte[] array = Wire.Extended((byte)hisMetadataId, ExtensionMessages.BuildMetadataReject(msg2.Piece));
								await stream.WriteAsync(array, 0, array.Length, _cts.Token).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (msg2.MsgType == 1)
						{
							if (!requested)
							{
								return "data without a request";
							}
							if (fullRequested)
							{
								if (AceLog.IsEnabled(LogLevel.Info))
								{
									AceLog.Info("metadata", cand.EndPoint?.ToString() + " full response: " + Describe(msg, msg2));
								}
								if (!msg2.Full)
								{
									return "only full metadata is supported";
								}
								byte[] array2 = ExtractFull(msg, msg2);
								if (array2 == null)
								{
									long? totalSize = msg2.TotalSize;
									return "bad total_size: " + totalSize;
								}
								if (_mode == MetadataMode.InfoHash)
								{
									return AcceptTransportForInfoHash(cand, msg, msg2, array2);
								}
								if (VerifySha1(array2))
								{
									AceLog.Info("metadata", "complete transport of " + _hashHex.Substring(0, 8) + ": " + array2.Length + " bytes from " + cand.EndPoint?.ToString() + " (" + cand.Source + "), SHA-1 = content_id");
									SetReport(exact: true, networks: 1, peers: 1, conflict: false, checksum: _hashHex);
									Complete(array2);
									return "done";
								}
								byte[] array3 = new byte[msg2.DataLength];
								Buffer.BlockCopy(msg, msg2.DataOffset, array3, 0, array3.Length);
								if (VerifySha1(array3))
								{
									SetReport(exact: true, networks: 1, peers: 1, conflict: false, checksum: _hashHex);
									Complete(array3);
									return "done";
								}
								DumpPayload(cand, array2);
								if (_ro.AcceptRequestHash && msg2.RequestHash != null && HashUtil.ConstantTimeEquals(msg2.RequestHash, _hash) && LooksLikeTransport(array2))
								{
									cand.Attempts = 99;
									AddWeak(cand, array2);
									return "done (different SHA-1; request_hash matches)";
								}
								AceLog.Warn("metadata", cand.EndPoint?.ToString() + ": SHA-1 of the data (" + Hex.ToHex(HashUtil.Sha1(array2)) + ") differs from " + _hashHex + ((msg2.RequestHash == null) ? " and no request_hash" : ("; request_hash " + (HashUtil.ConstantTimeEquals(msg2.RequestHash, _hash) ? "matches but the file cannot be interpreted" : "differs"))) + "; peer discarded");
								return "incorrect sha1";
							}
							int piece = msg2.Piece;
							string text = StoreBep9Piece(piece, msg, msg2);
							if (text == "done")
							{
								return "done";
							}
							if (text != null)
							{
								return text;
							}
							if (await RequestNextPieceAsync(stream, hisMetadataId).ConfigureAwait(continueOnCapturedContext: false) == -2)
							{
								return "done";
							}
						}
						else if (msg2.MsgType == 2)
						{
							if (!fullRequested)
							{
								ReleasePiece(msg2.Piece);
							}
							return "rejected";
						}
						goto end_IL_0071;
					}
					default:
						goto end_IL_0071;
					}
				}
				catch (OperationCanceledException)
				{
					if (_cts.IsCancellationRequested)
					{
						throw;
					}
					return "idle " + _ro.InactiveTimeout.TotalSeconds + " s";
				}
				end_IL_0071:;
			}
			return "done";
		}

		/// <summary>
		/// InfoHash mode: the peer answered 'full' with a transport file. It is accepted only if its infohash (that of the hash_fields of a live stream, or the SHA-1
		/// of the 'info' of a torrent) is the requested one: what is authenticated by that hash (name, broadcaster key, sizes, bitrate...) cannot be forged.
		/// request_hash does not count: here the real infohash was requested, not an alias. The data of a peer that does not match is discarded.
		/// </summary>
		private string AcceptTransportForInfoHash(Cand cand, byte[] msg, MetadataMessage mm, byte[] byTotalSize)
		{
			string reason;
			TransportFile tf = InfoHashVerifier.Match(byTotalSize, _hash, _ro.StrictTransport, out reason);
			if (tf == null && mm.DataLength != byTotalSize.Length && mm.DataLength > 0)
			{
				// Ace's total_size is that of the block; if it does not match what was received after the dictionary, everything that follows is tried
				byte[] all = new byte[mm.DataLength];
				Buffer.BlockCopy(msg, mm.DataOffset, all, 0, all.Length);
				string reason2;
				tf = InfoHashVerifier.Match(all, _hash, _ro.StrictTransport, out reason2);
				if (tf != null)
				{
					byTotalSize = all;
				}
			}
			if (tf == null)
			{
				DumpPayload(cand, byTotalSize);
				AceLog.Warn("metadata", cand.EndPoint?.ToString() + ": the received transport does not match the infohash " + _hashHex + " (" + reason + "); peer discarded");
				return "transport that does not match the infohash";
			}
			AceLog.Info("metadata", "transport of " + _hashHex.Substring(0, 8) + " (" + tf.Type + (tf.IsLive ? ", live" : "") + ", '" + tf.Name + "'): " + tf.RawBytes.Length + " bytes from " + cand.EndPoint?.ToString() + " (" + cand.Source + "), infohash verified");
			SetReport(exact: true, networks: 1, peers: 1, conflict: false, checksum: Hex.ToHex(HashUtil.Sha1(tf.RawBytes)));
			if (Report != null)
			{
				Report.Payload = MetadataPayload.Transport;
			}
			Complete(tf.RawBytes);
			return "done";
		}

		private bool LooksLikeTransport(byte[] data)
		{
			try
			{
				TransportLoader.Load(data, new TransportLoadOptions { Strict = _ro.StrictTransport });
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private void SetReport(bool exact, int networks, int peers, bool conflict, string checksum)
		{
			FetchReport report = Report;
			if (report != null)
			{
				report.ExactHash = exact;
				report.Networks = networks;
				report.Peers = peers;
				report.Conflict = conflict;
				report.Checksum = checksum;
			}
		}

		private void AddWeak(Cand cand, byte[] data)
		{
			string text = Hex.ToHex(HashUtil.Sha1(data));
			string text2 = TrustStore.NetworkKey(cand.EndPoint.Address);
			int count;
			int networks;
			bool flag = false;
			bool flag2 = false;
			lock (_gate)
			{
				if (!_weak.TryGetValue(text, out var value))
				{
					value = new Weak
					{
						Data = data,
						FirstMs = PeerConnection.NowMs()
					};
					_weak[text] = value;
				}
				value.Peers.Add(cand.EndPoint.ToString());
				value.Networks.Add(text2);
				count = value.Peers.Count;
				networks = value.Networks.Count;
				flag2 = _weak.Count > 1;
				// majority: there is a network quorum and no other file has as many networks as this one
				flag = _ro.WeakQuorum <= 1 || networks >= _ro.WeakQuorum;
				if (flag)
				{
					foreach (KeyValuePair<string, Weak> item in _weak)
					{
						if (item.Key != text && item.Value.Networks.Count >= networks)
						{
							flag = false;
						}
					}
				}
			}
			AceLog.Info("metadata", cand.EndPoint?.ToString() + ": transport of " + data.Length + " bytes with SHA-1 " + text + " (request_hash matches); " + count + " peer(s) in " + networks + " network(s) with the same bytes, quorum " + _ro.WeakQuorum + (flag2 ? " (there are different files: conflict)" : ""));
			if (flag)
			{
				AceLog.Info("metadata", "accepted by quorum (" + networks + " networks, " + count + " peers): " + text);
				SetReport(exact: false, networks: networks, peers: count, conflict: flag2, checksum: text);
				Complete(data);
			}
		}

		private void CheckWeakFallback()
		{
			if (_ro.WeakFallbackSeconds < 0.0)
			{
				return;
			}
			byte[] array = null;
			int num = 0;
			int peers = 0;
			bool tie = false;
			bool conflict;
			lock (_gate)
			{
				if (_done || _weak.Count == 0)
				{
					return;
				}
				long num2 = PeerConnection.NowMs();
				conflict = _weak.Count > 1;
				foreach (Weak value in _weak.Values)
				{
					if ((double)(num2 - value.FirstMs) < _ro.WeakFallbackSeconds * 1000.0)
					{
						continue;
					}
					if (value.Networks.Count > num)
					{
						num = value.Networks.Count;
						peers = value.Peers.Count;
						array = value.Data;
						tie = false;
					}
					else if (value.Networks.Count == num)
					{
						tie = true;
					}
				}
				// Two different files with the same support: neither is chosen (it keeps waiting for more peers or the deadline runs out).
				if (conflict && tie)
				{
					array = null;
				}
			}
			if (array != null)
			{
				AceLog.Warn("metadata", "no quorum after " + _ro.WeakFallbackSeconds + " s: accepting the transport delivered by " + peers + " peer(s) from " + num + " network(s) (SHA-1 " + Hex.ToHex(HashUtil.Sha1(array)) + " != content_id), UNCONFIRMED");
				SetReport(exact: false, networks: num, peers: peers, conflict: conflict, checksum: Hex.ToHex(HashUtil.Sha1(array)));
				Complete(array);
			}
		}

		private static string Describe(byte[] msg, MetadataMessage mm)
		{
			int val = mm.DataOffset - 2;
			string @string = Encoding.GetEncoding("iso-8859-1").GetString(msg, 2, Math.Max(0, Math.Min(val, 200)));
			string[] obj = new string[8]
			{
				"dict=",
				@string,
				" data=",
				mm.DataLength.ToString(),
				" B, total_size=",
				null,
				null,
				null
			};
			long? totalSize = mm.TotalSize;
			obj[5] = totalSize.ToString();
			obj[6] = ", first=";
			obj[7] = Hex.ToHex(msg, mm.DataOffset, Math.Min(24, mm.DataLength));
			return string.Concat(obj);
		}

		private void DumpPayload(Cand cand, byte[] data)
		{
			string dumpDir = _ro.DumpDir;
			if (string.IsNullOrEmpty(dumpDir))
			{
				return;
			}
			try
			{
				Directory.CreateDirectory(dumpDir);
				string text = Path.Combine(dumpDir, _hashHex + "_" + cand.EndPoint.Address?.ToString() + "_" + cand.EndPoint.Port + ".bin");
				File.WriteAllBytes(text, data);
				AceLog.Info("metadata", "discarded payload saved to " + text);
			}
			catch (Exception ex)
			{
				AceLog.Debug("metadata", "could not dump the payload: " + ex.Message);
			}
		}

		private bool VerifySha1(byte[] data)
		{
			if (data != null)
			{
				return HashUtil.ConstantTimeEquals(HashUtil.Sha1(data), _hash);
			}
			return false;
		}

		private static byte[] ExtractFull(byte[] msg, MetadataMessage mm)
		{
			long valueOrDefault = mm.TotalSize.GetValueOrDefault(-1L);
			if (valueOrDefault <= 0 || valueOrDefault > msg.Length)
			{
				return null;
			}
			byte[] array = new byte[valueOrDefault];
			Buffer.BlockCopy(msg, msg.Length - (int)valueOrDefault, array, 0, (int)valueOrDefault);
			return array;
		}

		private bool PrepareBep9(int size)
		{
			lock (_gate)
			{
				if (_buf == null)
				{
					_size = size;
					_buf = new byte[size];
					int num = (size + 16384 - 1) / 16384;
					_got = new bool[num];
					_reqAt = new long[num];
					return true;
				}
				return size == _size;
			}
		}

		private async Task<int> RequestNextPieceAsync(NetworkStream stream, int hisId)
		{
			int pick = -1;
			lock (_gate)
			{
				if (_got.All((bool g) => g))
				{
					return -2;
				}
				long num = PeerConnection.NowMs();
				for (int i = 0; i < _got.Length; i++)
				{
					if (!_got[i] && (_reqAt[i] == 0L || num - _reqAt[i] >= 10000))
					{
						_reqAt[i] = num;
						pick = i;
						break;
					}
				}
			}
			if (pick < 0)
			{
				return -1;
			}
			byte[] array = Wire.Extended((byte)hisId, ExtensionMessages.BuildMetadataRequest(pick));
			await stream.WriteAsync(array, 0, array.Length, _cts.Token).ConfigureAwait(continueOnCapturedContext: false);
			return pick;
		}

		private void ReleasePiece(int piece)
		{
			lock (_gate)
			{
				if (_reqAt != null && piece >= 0 && piece < _reqAt.Length)
				{
					_reqAt[piece] = 0L;
				}
			}
		}

		private string StoreBep9Piece(int piece, byte[] msg, MetadataMessage mm)
		{
			byte[] array = null;
			lock (_gate)
			{
				if (_buf == null || piece < 0 || piece >= _got.Length)
				{
					return "piece out of range";
				}
				int num = piece * 16384;
				int num2 = Math.Min(16384, _size - num);
				if (mm.DataLength != num2)
				{
					_reqAt[piece] = 0L;
					return "piece " + piece + " of " + mm.DataLength + " bytes (expected " + num2 + ")";
				}
				if (!_got[piece])
				{
					Buffer.BlockCopy(msg, mm.DataOffset, _buf, num, num2);
					_got[piece] = true;
				}
				if (_got.All((bool g) => g))
				{
					if (!VerifySha1(_buf))
					{
						AceLog.Warn("metadata", "BEP9 metadata with a SHA-1 different from the infohash; discarded");
						_buf = null;
						_got = null;
						_reqAt = null;
						_size = 0;
						return "incorrect sha1";
					}
					array = _buf;
				}
			}
			if (array != null)
			{
				AceLog.Info("metadata", "BEP9 info of " + _hashHex.Substring(0, 8) + ": " + array.Length + " bytes");
				if (Report != null)
				{
					Report.Payload = MetadataPayload.Info;
				}
				Complete(array);
				return "done";
			}
			return null;
		}

		private static async Task ReadExactAsync(NetworkStream s, byte[] buf, int count, CancellationToken ct)
		{
			int num;
			for (int got = 0; got < count; got += num)
			{
				num = await s.ReadAsync(buf, got, count - got, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (num == 0)
				{
					throw new EndOfStreamException();
				}
			}
		}
	}

	private const string Tag = "metadata";

	private readonly EngineOptions _opt;

	private readonly DhtClient _dht;

	private readonly HttpClient _http;

	private int _activeConnections;

	private int _peakConnections;

	private long _connectionAttempts;

	public MetadataFetcher(EngineOptions options, DhtClient dht, HttpClient http)
	{
		_opt = options;
		_dht = dht;
		_http = http;
	}

	/// <summary>TCP resolution connections open right now, across all resolutions at once.</summary>
	public int ActiveConnections => Volatile.Read(ref _activeConnections);

	/// <summary>Maximum simultaneous connections observed.</summary>
	public int PeakConnections => Volatile.Read(ref _peakConnections);

	/// <summary>Connections attempted in total.</summary>
	public long ConnectionAttempts => Interlocked.Read(ref _connectionAttempts);

	/// <summary>
	/// Counts the connections of all the resolutions. There is no shared cap: with a "first come, first served" split some resolutions were left without
	/// connections and were considered not served. Each resolution has its own budget (ResolveProfile.MaxConnections) and the prober only admits
	/// as many at once as fit in the total budget (ProbeOptions.ResolveConnectionBudget).
	/// </summary>
	internal void NoteConnectionStart()
	{
		int now = Interlocked.Increment(ref _activeConnections);
		Interlocked.Increment(ref _connectionAttempts);
		int peak;
		while (now > (peak = Volatile.Read(ref _peakConnections)))
		{
			Interlocked.CompareExchange(ref _peakConnections, now, peak);
		}
	}

	internal void NoteConnectionEnd()
	{
		Interlocked.Decrement(ref _activeConnections);
	}

	public async Task<byte[]> FetchAsync(byte[] hash, MetadataMode mode, IEnumerable<string> trackers, IEnumerable<IPEndPoint> peers, TimeSpan timeout, CancellationToken ct, ICollection<IPEndPoint> goodPeers = null, FetchReport report = null, ResolveProfile profile = null)
	{
		if (hash == null || hash.Length != 20)
		{
			throw new ArgumentException("the hash must be 20 bytes", "hash");
		}
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(new CancellationToken[1] { ct });
		if (timeout > TimeSpan.Zero)
		{
			cts.CancelAfter(timeout);
		}
		Job job = new Job(this, hash, mode, cts)
		{
			GoodPeers = goodPeers,
			Report = report,
			Profile = (profile ?? new ResolveProfile())
		};
		if (trackers != null)
		{
			job.Trackers.AddRange(trackers.Where((string t) => !string.IsNullOrEmpty(t)));
		}
		if (peers != null)
		{
			job.AddCandidates(peers, "fixed");
		}
		try
		{
			return await job.RunAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
			if (ct.IsCancellationRequested)
			{
				throw;
			}
			return null;
		}
		finally
		{
			cts.Cancel();
			job.Finish();
		}
	}
}
