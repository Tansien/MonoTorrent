//
// Mode.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2009 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

using MonoTorrent.BEncoding;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Connections.Peer.Encryption;
using MonoTorrent.Logging;
using MonoTorrent.Messages;
using MonoTorrent.Messages.Peer;
using MonoTorrent.Messages.Peer.FastPeer;
using MonoTorrent.Messages.Peer.Libtorrent;
using MonoTorrent.PiecePicking;

using ReusableTasks;

namespace MonoTorrent.Client.Modes
{
    abstract class Mode
    {
        static readonly Logger logger = Logger.Create (nameof (Mode));
        static readonly SHA1 AllowedFastHasher = SHA1.Create ();

        bool hashingPendingFiles;


        protected CancellationTokenSource Cancellation { get; }
        protected ConnectionManager ConnectionManager { get; }
        protected DiskManager DiskManager { get; }
        protected TorrentManager Manager { get; }
        protected EngineSettings Settings { get; }
        protected IUnchoker Unchoker { get; }

        public virtual bool CanAcceptConnections => true;
        public virtual bool CanHandleMessages => true;
        public virtual bool CanHashCheck => false;
        public abstract TorrentState State { get; }
        public CancellationToken Token => Cancellation.Token;

        protected Mode (TorrentManager manager, DiskManager diskManager, ConnectionManager connectionManager, EngineSettings settings, IUnchoker? unchoker = null)
        {
            Cancellation = new CancellationTokenSource ();
            ConnectionManager = connectionManager;
            DiskManager = diskManager;
            Manager = manager;
            Settings = settings;

            Unchoker = unchoker ?? new ChokeUnchokeManager (new TorrentManagerUnchokeable (manager));
        }

        public void HandleMessage (PeerId id, PeerMessage message, PeerMessage.Releaser releaser)
        {
            if (!CanHandleMessages)
                return;

            if (message is IFastPeerMessage && !id.SupportsFastPeer)
                throw new MessageException ("Peer shouldn't support fast peer messages");

            if (message is ExtensionMessage && !id.SupportsLTMessages && !(message is ExtendedHandshakeMessage))
                throw new MessageException ("Peer shouldn't support extension messages");

            if (message is HaveMessage have)
                HandleHaveMessage (id, have);
            else if (message is RequestMessage request)
                HandleRequestMessage (id, request);
            else if (message is PortMessage port)
                HandlePortMessage (id, port);
            else if (message is PieceMessage piece)
                HandlePieceMessage (id, piece, releaser);
            else if (message is NotInterestedMessage notinterested)
                HandleNotInterested (id, notinterested);
            else if (message is KeepAliveMessage keepalive)
                HandleKeepAliveMessage (id, keepalive);
            else if (message is InterestedMessage interested)
                HandleInterestedMessage (id, interested);
            else if (message is ChokeMessage choke)
                HandleChokeMessage (id, choke);
            else if (message is CancelMessage cancel)
                HandleCancelMessage (id, cancel);
            else if (message is BitfieldMessage bitfield)
                HandleBitfieldMessage (id, bitfield);
            else if (message is UnchokeMessage unchoke)
                HandleUnchokeMessage (id, unchoke);
            else if (message is HashRejectMessage hashReject)
                HandleHashRejectMessage (id, hashReject);
            else if (message is HashesMessage hashes)
                HandleHashesMessage (id, hashes);
            else if (message is HashRequestMessage hashRequest)
                HandleHashRequestMessage (id, hashRequest);
            else if (message is HaveAllMessage haveall)
                HandleHaveAllMessage (id, haveall);
            else if (message is HaveNoneMessage havenone)
                HandleHaveNoneMessage (id, havenone);
            else if (message is RejectRequestMessage rejectrequest)
                HandleRejectRequestMessage (id, rejectrequest);
            else if (message is SuggestPieceMessage suggestpiece)
                HandleSuggestedPieceMessage (id, suggestpiece);
            else if (message is AllowedFastMessage allowedfast)
                HandleAllowedFastMessage (id, allowedfast);
            else if (message is ExtendedHandshakeMessage extendedhandshake)
                HandleExtendedHandshakeMessage (id, extendedhandshake);
            else if (message is LTMetadata metadata)
                HandleLtMetadataMessage (id, metadata);
            else if (message is LTChat chat)
                HandleLtChat (id, chat);
            else if (message is PeerExchangeMessage peerexchange)
                HandlePeerExchangeMessage (id, peerexchange);
            else if (message is HandshakeMessage handshake)
                HandleHandshakeMessage (id, handshake);
            else if (message is ExtensionMessage extension)
                HandleGenericExtensionMessage (id, extension);
            else
                throw new MessageException ($"Unsupported message found: {message.GetType ().Name}");

            if (!(message is PieceMessage))
                releaser.Dispose ();
            ConnectionManager.TryProcessQueue (Manager, id);
        }

        protected virtual void HandleHashRequestMessage (PeerId id, HashRequestMessage hashRequest)
        {
            id.MessageQueue.Enqueue (new HashRejectMessage (hashRequest.PiecesRoot, hashRequest.BaseLayer, hashRequest.Index, hashRequest.Length, hashRequest.ProofLayers));
        }

        protected virtual void HandleHashesMessage (PeerId id, HashesMessage hashesMessage)
        {
        }

        protected virtual void HandleHashRejectMessage (PeerId id, HashRejectMessage hashRejectMessage)
        {
        }

        public bool ShouldConnect (PeerId peer)
        {
            return ShouldConnect (peer.Peer);
        }

        public virtual bool ShouldConnect (Peer peer)
        {
            return true;
        }

        protected virtual void HandleGenericExtensionMessage (PeerId id, ExtensionMessage extensionMessage)
        {
            // Do nothing
        }

        protected virtual void HandleHandshakeMessage (PeerId id, HandshakeMessage message)
        {
            if (!message.ProtocolString.Equals (Constants.ProtocolStringV100)) {
                logger.InfoFormatted (id.Connection, "Invalid protocol in handshake: {0}", message.ProtocolString);
                throw new ProtocolException ("Invalid protocol string");
            }

            // If we got the peer as a "compact" peer, then the peerid will be empty. In this case
            // we just copy the one that is in the handshake.
            if (BEncodedString.IsNullOrEmpty (id.Peer.PeerId))
                id.Peer.PeerId = message.PeerId;

            // If the infohash doesn't match, dump the connection
            if (!Manager.InfoHashes.Contains (message.InfoHash)) {
                logger.Info (id.Connection, "HandShake.Handle - Invalid infohash");
                throw new TorrentException ("Invalid infohash. Not tracking this torrent");
            }

            // If the peer id's don't match, dump the connection. This is due to peers faking usually
            if (!id.Peer.PeerId.Equals (message.PeerId)) {
                if (Manager.HasMetadata && Manager.Torrent!.IsPrivate) {
                    // If this is a private torrent we should be careful about peerids. If they don't
                    // match we should close the connection. I *think* uTorrent doesn't randomise peerids
                    // for private torrents. It's not documented very well. We may need to relax this check
                    // if other clients randomize for private torrents.
                    logger.Info (id.Connection, "HandShake.Handle - Invalid peerid");
                    throw new TorrentException ("Supplied PeerID didn't match the one the tracker gave us");
                } else {
                    // We don't care about the mismatch for public torrents. uTorrent randomizes its PeerId, as do other clients.
                    id.Peer.PeerId = message.PeerId;
                }
            }

            // Attempt to parse the application that the peer is using
            id.ClientApp = new Software (message.PeerId);
            id.SupportsFastPeer = message.SupportsFastPeer;
            id.SupportsLTMessages = message.SupportsExtendedMessaging;

            // If they support fast peers, create their list of allowed pieces that they can request off me
            if (id.SupportsFastPeer && id.AddressBytes != null && Manager != null && Manager.HasMetadata) {
                lock (AllowedFastHasher)
                    id.AmAllowedFastPieces = AllowedFastAlgorithm.Calculate (AllowedFastHasher, id.AddressBytes, Manager.InfoHashes, (uint) Manager.Torrent!.PieceCount);
            }
        }

        protected virtual async void HandlePeerExchangeMessage (PeerId id, PeerExchangeMessage message)
        {
            // Ignore peer exchange messages on private toirrents
            if ((Manager.Torrent != null && Manager.Torrent.IsPrivate) || !Manager.Settings.AllowPeerExchange) {
                Manager.RaisePeersFound (new PeerExchangePeersAdded (Manager, 0, 0, id));
            } else {
                // If we already have lots of peers, don't process the messages anymore.
                if ((Manager.Peers.Available + Manager.OpenConnections) >= Manager.Settings.MaximumConnections)
                    return;

                IList<Peer> newPeers = Peer.Decode (BEncodedString.FromMemory (message.Added));
                for (int i = 0; i < newPeers.Count && i < message.AddedDotF.Length; i++) {
                    newPeers[i].IsSeeder = (message.AddedDotF.Span[i] & 0x2) == 0x2;
                }
                int count = await Manager.AddPeersAsync (newPeers);
                Manager.RaisePeersFound (new PeerExchangePeersAdded (Manager, count, newPeers.Count, id));
            }
        }

        protected virtual void HandleLtChat (PeerId id, LTChat message)
        {

        }

        protected virtual void HandleLtMetadataMessage (PeerId id, LTMetadata message)
        {
            if (message.MetadataMessageType == LTMetadata.MessageType.Request) {
                id.MessageQueue.Enqueue (Manager.HasMetadata
                    ? new LTMetadata (id.ExtensionSupports, LTMetadata.MessageType.Data, message.Piece, Manager.Torrent!.InfoMetadata)
                    : new LTMetadata (id.ExtensionSupports, LTMetadata.MessageType.Reject, message.Piece));
            }
        }

        protected virtual void HandleAllowedFastMessage (PeerId id, AllowedFastMessage message)
        {
            if (!Manager.Bitfield[message.PieceIndex])
                id.IsAllowedFastPieces.Add (message.PieceIndex);
        }

        protected virtual void HandleSuggestedPieceMessage (PeerId id, SuggestPieceMessage message)
        {
            id.SuggestedPieces.Add (message.PieceIndex);
        }

        protected virtual void HandleRejectRequestMessage (PeerId id, RejectRequestMessage message)
        {
            Manager.PieceManager.RequestRejected (id, new BlockInfo (message.PieceIndex, message.StartOffset, message.RequestLength));
        }

        protected virtual void HandleHaveNoneMessage (PeerId id, HaveNoneMessage message)
        {
            id.MutableBitField.SetAll (false);
            id.Peer.IsSeeder = false;
            SetAmInterestedStatus (id, false);
        }

        protected virtual void HandleHaveAllMessage (PeerId id, HaveAllMessage message)
        {
            id.MutableBitField.SetAll (true);
            id.Peer.IsSeeder = true;
            SetAmInterestedStatus (id, Manager.PieceManager.IsInteresting (id));
        }

        protected virtual void HandleUnchokeMessage (PeerId id, UnchokeMessage message)
        {
            id.IsChoking = false;

            // Add requests to the peers message queue
            Manager.PieceManager.AddPieceRequests (id);
        }

        protected virtual void HandleBitfieldMessage (PeerId id, BitfieldMessage message)
        {
            id.MutableBitField.From (message.BitField);
            id.Peer.IsSeeder = (id.BitField.AllTrue);

            SetAmInterestedStatus (id, Manager.PieceManager.IsInteresting (id));
        }

        protected virtual void HandleCancelMessage (PeerId id, CancelMessage message)
        {
            if (id.MessageQueue.TryCancelRequest (message.PieceIndex, message.StartOffset, message.RequestLength))
                Interlocked.Decrement (ref id.isRequestingPiecesCount);
        }

        protected virtual void HandleChokeMessage (PeerId id, ChokeMessage message)
        {
            id.IsChoking = true;
            if (!id.SupportsFastPeer)
                Manager.PieceManager.CancelRequests (id);
        }

        protected virtual void HandleInterestedMessage (PeerId id, InterestedMessage message)
        {
            id.IsInterested = true;
        }

        protected virtual void HandleExtendedHandshakeMessage (PeerId id, ExtendedHandshakeMessage message)
        {
            // FIXME: Use the 'version' information
            // FIXME: Recreate the uri? Give warning?
            if (message.LocalPort > 0)
                id.Peer.LocalPort = message.LocalPort;

            // If MaxRequests is zero, or negative, ignore it.
            if (message.MaxRequests > 0)
                id.MaxSupportedPendingRequests = message.MaxRequests;
            else
                logger.InfoFormatted (id.Connection, "Invalid value for libtorrent extension handshake 'MaxRequests' {0}", message.MaxRequests);

            // Bugfix for MonoTorrent older than 1.0.19
            if (id.ClientApp.Client == ClientApp.MonoTorrent)
                id.MaxSupportedPendingRequests = Math.Max (id.MaxSupportedPendingRequests, 192);

            id.ExtensionSupports = message.Supports;

            if (id.ExtensionSupports.Supports (PeerExchangeMessage.Support.Name)) {
                if (Manager.HasMetadata && !Manager.Torrent!.IsPrivate)
                    id.PeerExchangeManager = new PeerExchangeManager (Manager, id);
            }
        }

        protected virtual void HandleKeepAliveMessage (PeerId id, KeepAliveMessage message)
        {
            id.LastMessageReceived.Restart ();
        }

        protected virtual void HandleNotInterested (PeerId id, NotInterestedMessage message)
        {
            id.IsInterested = false;
        }

        protected virtual void HandlePieceMessage (PeerId id, PieceMessage message, PeerMessage.Releaser releaser)
        {
            id.PiecesReceived++;
            if (Manager.PieceManager.PieceDataReceived (id, message, out bool _, out IList<IPeer> peersInvolved))
                WritePieceAsync (message, releaser, peersInvolved);
            else
                releaser.Dispose ();
            // Keep adding new piece requests to this peers queue until we reach the max pieces we're allowed queue
            Manager.PieceManager.AddPieceRequests (id);
        }

        readonly Dictionary<int, (int blocksWritten, IList<IPeer> peersInvolved)> BlocksWrittenPerPiece = new Dictionary<int, (int blocksWritten, IList<IPeer> peersInvolved)> ();
        async void WritePieceAsync (PieceMessage message, PeerMessage.Releaser releaser, IList<IPeer> peersInvolved)
        {
            BlockInfo block = new BlockInfo (message.PieceIndex, message.StartOffset, message.RequestLength);
            try {
                using (releaser)
                    await DiskManager.WriteAsync (Manager, block, message.Data);
                if (Cancellation.IsCancellationRequested)
                    return;
            } catch (Exception ex) {
                Manager.TrySetError (Reason.WriteFailure, ex);
                return;
            }

            if (!BlocksWrittenPerPiece.TryGetValue (block.PieceIndex, out (int blocksWritten, IList<IPeer> peersInvolved) data))
                data = (0, peersInvolved);

            // Increment the number of blocks, and keep storing 'peersInvolved' until it's non-null. It will be non-null when the
            // final piece is received.
            data = (data.blocksWritten + 1, data.peersInvolved ?? peersInvolved);
            if (data.blocksWritten != Manager.Torrent!.BlocksPerPiece (block.PieceIndex)) {
                BlocksWrittenPerPiece[block.PieceIndex] = data;
                return;
            }

            // All blocks have been written for this piece have been written!
            BlocksWrittenPerPiece.Remove (block.PieceIndex);
            peersInvolved = data.peersInvolved;

            // Hashcheck the piece as we now have all the blocks.
            // BEP52: Support validating both SHA1 *and* SHA256.
            using var byteBuffer = MemoryPool.Default.Rent (Manager.InfoHashes.GetMaxByteCount (), out Memory<byte> hashMemory);
            var hashes = new PieceHash (hashMemory);
            bool successful = false;
            try {
                successful = await DiskManager.GetHashAsync (Manager, block.PieceIndex, hashes);
                if (Cancellation.IsCancellationRequested)
                    return;
            } catch (Exception ex) {
                Manager.TrySetError (Reason.ReadFailure, ex);
                return;
            }

            bool result = successful && Manager.PieceHashes.IsValid (hashes, block.PieceIndex);
            Manager.OnPieceHashed (block.PieceIndex, result, 1, 1);
            Manager.PieceManager.PieceHashed (block.PieceIndex);
            if (!result)
                Manager.HashFails++;

            for (int i = 0; i < peersInvolved.Count; i ++) {
                var peer = (PeerId) peersInvolved[i];
                peer.Peer.HashedPiece (result);
                if (peer.Peer.TotalHashFails == 5)
                    ConnectionManager.CleanupSocket (Manager, peer);
            }

            // If the piece was successfully hashed, enqueue a new "have" message to be sent out
            if (result)
                Manager.finishedPieces.Enqueue (block.PieceIndex);
        }

        protected virtual void HandlePortMessage (PeerId id, PortMessage message)
        {
            id.Port = (ushort) message.Port;
        }

        protected virtual void HandleRequestMessage (PeerId id, RequestMessage message)
        {
            // If we are not on the last piece and the user requested a stupidly big/small amount of data
            // we will close the connection
            if (Manager.Torrent!.PieceCount != (message.PieceIndex + 1))
                if (message.RequestLength > RequestMessage.MaxSize || message.RequestLength < RequestMessage.MinSize)
                    throw new MessageException (
                        $"Illegal piece request received. Peer requested {message.RequestLength} byte");



            // If we're not choking the peer, enqueue the message right away
            if (!id.AmChoking) {
                Interlocked.Increment (ref id.isRequestingPiecesCount);
                (var m, var releaser) = PeerMessage.Rent<PieceMessage> ();
                m.Initialize (message.PieceIndex, message.StartOffset, message.RequestLength);
                id.MessageQueue.Enqueue (m, releaser);
            }

            // If the peer supports fast peer and the requested piece is one of the allowed pieces, enqueue it
            // otherwise send back a reject request message
            else if (id.SupportsFastPeer) {
                if (id.AmAllowedFastPieces.Contains (message.PieceIndex)) {
                    Interlocked.Increment (ref id.isRequestingPiecesCount);
                    (var m, var releaser) = PeerMessage.Rent<PieceMessage> ();
                    m.Initialize (message.PieceIndex, message.StartOffset, message.RequestLength);
                    id.MessageQueue.Enqueue (m, releaser);
                } else {
                    (var m, var releaser) = PeerMessage.Rent<RejectRequestMessage> ();
                    m.Initialize (message.PieceIndex, message.StartOffset, message.RequestLength);
                    id.MessageQueue.Enqueue (m, releaser);
                }
            }
        }

        protected virtual void HandleHaveMessage (PeerId id, HaveMessage message)
        {
            id.HaveMessageEstimatedDownloadedBytes += Manager.Torrent!.PieceLength;

            // First set the peers bitfield to true for that piece
            id.MutableBitField[message.PieceIndex] = true;

            // Fastcheck to see if a peer is a seeder or not
            id.Peer.IsSeeder = id.BitField.AllTrue;

            // We can do a fast check to see if the peer is interesting or not when we receive a Have Message.
            // If the peer just received a piece we don't have, he's interesting. Otherwise his state is unchanged
            if (!Manager.Bitfield[message.PieceIndex])
                SetAmInterestedStatus (id, true);
        }

        public virtual void HandlePeerConnected (PeerId id)
        {
            if (CanAcceptConnections) {
                (var bundle, var releaser) = PeerMessage.Rent<MessageBundle> ();

                AppendBitfieldMessage (id, bundle);
                AppendExtendedHandshake (id, bundle);
                AppendFastPieces (id, bundle);

                id.MessageQueue.Enqueue (bundle, releaser);
            } else {
                ConnectionManager.CleanupSocket (Manager, id);
            }
        }

        public virtual void HandlePeerDisconnected (PeerId id)
        {

        }

        protected virtual void AppendExtendedHandshake (PeerId id, MessageBundle bundle)
        {
            if (id.SupportsLTMessages)
                bundle.Add (new ExtendedHandshakeMessage (Manager.Torrent?.IsPrivate ?? false, Manager.Torrent != null ? Manager.Torrent.InfoMetadata.Length : (int?) null, Settings.ListenEndPoint?.Port ?? -1), default);
        }

        protected virtual void AppendFastPieces (PeerId id, MessageBundle bundle)
        {
            // Now we will enqueue a FastPiece message for each piece we will allow the peer to download
            // even if they are choked
            if (id.SupportsFastPeer) {
                for (int i = 0; i < id.AmAllowedFastPieces.Count; i++) {
                    (var msg, var releaser) = PeerMessage.Rent<AllowedFastMessage> ();
                    msg.Initialize (id.AmAllowedFastPieces[i]);
                    bundle.Add (msg, releaser);
                }
            }
        }

        protected virtual void AppendBitfieldMessage (PeerId id, MessageBundle bundle)
        {
            if (id.SupportsFastPeer) {
                if (Manager.Bitfield.AllFalse)
                    bundle.Add (HaveNoneMessage.Instance, default);

                else if (Manager.Bitfield.AllTrue)
                    bundle.Add (HaveAllMessage.Instance, default);

                else
                    bundle.Add (new BitfieldMessage (Manager.Bitfield), default);
            } else {
                bundle.Add (new BitfieldMessage (Manager.Bitfield), default);
            }
        }

        public virtual void Tick (int counter)
        {
            PreLogicTick (counter);
            if (Manager.State == TorrentState.Downloading)
                DownloadLogic (counter);
            else if (Manager.State == TorrentState.Seeding)
                SeedingLogic (counter);
            PostLogicTick (counter);

        }

        void PreLogicTick (int counter)
        {
            PeerId id;
            if (Manager.Engine == null)
                return;

            // If any files were changed from DoNotDownload -> Any other priority, then we should hash them if they
            // had been skipped in the original hashcheck.
            _ = TryHashPendingFilesAsync ();

            if (Manager.CanUseLocalPeerDiscovery && (!Manager.LastLocalPeerAnnounceTimer.IsRunning || Manager.LastLocalPeerAnnounceTimer.Elapsed > Manager.Engine.LocalPeerDiscovery.AnnounceInternal)) {
                _ = Manager.LocalPeerAnnounceAsync ();
            }

            if (Manager.CanUseDht && (!Manager.LastDhtAnnounceTimer.IsRunning || Manager.LastDhtAnnounceTimer.Elapsed > Manager.Engine.DhtEngine.AnnounceInterval)) {
                Manager.DhtAnnounce ();
            }

            //Execute iniitial logic for individual peers
            if (counter % (1000 / ClientEngine.TickLength) == 0) {   // Call it every second... ish
                Manager.Monitor.Tick ();
                Manager.UpdateLimiters ();
            }

            Manager.Peers.UpdatePeerCounts ();

            if (Manager.finishedPieces.Count > 0)
                SendHaveMessagesToAll ();

            for (int i = 0; i < Manager.Peers.ConnectedPeers.Count; i++) {
                id = Manager.Peers.ConnectedPeers[i];
                if (id.Connection == null)
                    continue;

                if (!id.LastPeerExchangeReview.IsRunning || id.LastPeerExchangeReview.Elapsed > TimeSpan.FromMinutes (1)) {
                    id.PeerExchangeManager?.OnTick ();
                    id.LastPeerExchangeReview.Restart ();
                }

                int maxRequests = PieceManager.NormalRequestAmount + (int) (id.Monitor.DownloadRate / 1024.0 / PieceManager.BonusRequestPerKb);
                maxRequests = Math.Min (id.MaxSupportedPendingRequests, maxRequests);
                maxRequests = Math.Max (2, maxRequests);
                id.MaxPendingRequests = maxRequests;

                id.Monitor.Tick ();
            }
        }

        void PostLogicTick (int counter)
        {
            PeerId id;

            var fifteenSeconds = TimeSpan.FromSeconds (15);
            var ninetySeconds = TimeSpan.FromSeconds (90);
            var onhundredAndEightySeconds = TimeSpan.FromSeconds (180);

            for (int i = 0; i < Manager.Peers.ConnectedPeers.Count; i++) {
                id = Manager.Peers.ConnectedPeers[i];
                if (id.Connection == null)
                    continue;

                ConnectionManager.TryProcessQueue (Manager, id);

                if (id.LastMessageSent.Elapsed > ninetySeconds) {
                    id.LastMessageSent.Restart ();
                    id.MessageQueue.Enqueue (KeepAliveMessage.Instance, default);
                }

                if (id.LastMessageReceived.Elapsed > onhundredAndEightySeconds) {
                    ConnectionManager.CleanupSocket (Manager, id);
                    i--;
                    continue;
                }

                if (id.LastBlockReceived.Elapsed > fifteenSeconds && id.AmRequestingPiecesCount > 0) {
                    ConnectionManager.CleanupSocket (Manager, id);
                    i--;
                    continue;
                }
            }

            Manager.PieceManager.AddPieceRequests (Manager.Peers.ConnectedPeers);

            if (Manager.State == TorrentState.Seeding || Manager.State == TorrentState.Downloading) {
                _ = Manager.TrackerManager.AnnounceAsync (TorrentEvent.None, CancellationToken.None);
            }
        }

        void DownloadLogic (int counter)
        {
            if (ClientEngine.SupportsWebSeed && (DateTime.Now - Manager.StartTime) > Manager.Settings.WebSeedDelay && Manager.Monitor.DownloadRate < Manager.Settings.WebSeedSpeedTrigger) {
                foreach (Uri uri in Manager.Torrent!.HttpSeeds) {
                    BEncodedString peerId = CreatePeerId ();

                    var peer = new Peer (peerId, uri);

                    var connection = new HttpPeerConnection (Manager, Manager.Engine!.Factories, uri);
                    // Unsupported connection type.
                    if (connection == null)
                        continue;

                    var id = new PeerId (peer, connection, new BitField (Manager.Bitfield.Length).SetAll (true));
                    id.Encryptor = PlainTextEncryption.Instance;
                    id.Decryptor = PlainTextEncryption.Instance;
                    id.IsChoking = false;
                    id.ClientApp = new Software (id.PeerID);
                    Manager.Peers.ConnectedPeers.Add (id);
                    Interlocked.Increment (ref ConnectionManager.openConnections);
                    Manager.RaisePeerConnected (new PeerConnectedEventArgs (Manager, id));
                    ConnectionManager.ReceiveMessagesAsync (id.Connection, id.Decryptor, Manager.DownloadLimiters, id.Monitor, Manager, id);
                    if (!Manager.Complete) {
                        SetAmInterestedStatus (id, true);
                        id.MessageQueue.SetReady ();
                        ConnectionManager.TryProcessQueue (Manager, id);
                    }
                }

                // FIXME: In future, don't clear out this list. It may be useful to keep the list of HTTP seeds
                // Add a boolean or something so that we don't add them twice.
                Manager.Torrent.HttpSeeds.Clear ();
            }

            // Remove inactive peers we haven't heard from if we're downloading
            if (Manager.State == TorrentState.Downloading && Manager.lastCalledInactivePeerManager + TimeSpan.FromSeconds (5) < DateTime.Now) {
                Manager.InactivePeerManager.TimePassed ();
                Manager.lastCalledInactivePeerManager = DateTime.Now;
            }

            Unchoker.UnchokeReview ();
        }

        void SeedingLogic (int counter)
        {
            Unchoker.UnchokeReview ();
        }

        protected virtual void SetAmInterestedStatus (PeerId id, bool interesting)
        {
            if (interesting && !id.AmInterested) {
                id.AmInterested = true;
                id.MessageQueue.Enqueue (InterestedMessage.Instance, default);

                // He's interesting, so attempt to queue up any FastPieces (if that's possible)
                Manager.PieceManager.AddPieceRequests (id);
            } else if (!interesting && id.AmInterested) {
                id.AmInterested = false;
                id.MessageQueue.Enqueue (NotInterestedMessage.Instance, default);
            }
        }

        internal async ReusableTask TryHashPendingFilesAsync ()
        {
            // If we cannot handle peer messages then we should not try to async hash.
            // This adds a little bit of a double meaning to the property (for now).
            // Any mode which doesn't allow processing peer messages also does not allow
            // partial hashing.
            if (hashingPendingFiles || !Manager.HasMetadata || !CanHandleMessages)
                return;

            // FIXME: Handle errors from DiskManager and also handle cancellation if the Mode is replaced.
            hashingPendingFiles = true;
            try {
                using var hashBuffer = MemoryPool.Default.Rent (Manager.InfoHashes.GetMaxByteCount (), out Memory<byte> hashMemory);
                var hashes = new PieceHash (hashMemory);
                foreach (var file in Manager.Files) {
                    // If the start piece *and* end piece have been hashed, then every piece in between must've been hashed!
                    if (file.Priority != Priority.DoNotDownload && (Manager.UnhashedPieces[file.StartPieceIndex] || Manager.UnhashedPieces[file.EndPieceIndex])) {
                        for (int index = file.StartPieceIndex; index <= file.EndPieceIndex; index++) {
                            if (Manager.UnhashedPieces[index]) {
                                var successful = await DiskManager.GetHashAsync (Manager, index, hashes);
                                Cancellation.Token.ThrowIfCancellationRequested ();

                                bool hashPassed = successful && Manager.PieceHashes.IsValid (hashes, index);
                                Manager.OnPieceHashed (index, hashPassed, 1, 1);

                                if (hashPassed)
                                    Manager.finishedPieces.Enqueue (index);
                            }
                        }
                    }
                }
            } finally {
                hashingPendingFiles = false;
            }
        }

        void SendHaveMessagesToAll ()
        {
            if (Manager.finishedPieces.Count == 0)
                return;

            foreach (PeerId peer in Manager.Peers.ConnectedPeers) {
                (var bundle, var releaser) = PeerMessage.Rent<HaveBundle> ();
                foreach (int pieceIndex in Manager.finishedPieces)
                    if (!Settings.AllowHaveSuppression || !peer.BitField[pieceIndex])
                        bundle.Add (pieceIndex);

                if (bundle.Count == 0)
                    releaser.Dispose ();
                else
                    peer.MessageQueue.Enqueue (bundle, releaser);
            }

            foreach (PeerId peer in Manager.Peers.ConnectedPeers) {
                bool isInteresting = Manager.PieceManager.IsInteresting (peer);
                SetAmInterestedStatus (peer, isInteresting);
            }
            Manager.finishedPieces.Clear ();
        }

        public void Dispose ()
        {
            Cancellation.Cancel ();
        }


        static int webSeedId;
        internal static BEncodedString CreatePeerId ()
        {
            string peerId = "-WebSeed-";
            peerId += Interlocked.Increment (ref webSeedId).ToString ().PadLeft (20 - peerId.Length, '0');
            return peerId;
        }
    }
}
