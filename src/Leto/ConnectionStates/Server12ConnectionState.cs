﻿using System;
using System.IO.Pipelines;
using Leto.Handshake;
using Leto.RecordLayer;
using Leto.CipherSuites;
using Leto.Handshake.Extensions;
using Leto.KeyExchanges;
using Leto.Hashes;
using System.Threading.Tasks;
using Leto.Certificates;
using Leto.BulkCiphers;
using Leto.ConnectionStates.SecretSchedules;
using System.Binary;

namespace Leto.ConnectionStates
{
    public sealed partial class Server12ConnectionState : IConnectionState
    {
        private SecurePipeConnection _secureConnection;
        private ApplicationLayerProtocolType _negotiatedAlpn;
        private bool _secureRenegotiation;
        private IHash _handshakeHash;
        private ICertificate _certificate;
        private SignatureScheme _signatureScheme;
        private HandshakeState _state;
        private SecretSchedule12 _secretSchedule;
        private AeadBulkCipher _readKey;
        private AeadBulkCipher _writeKey;
        private AeadBulkCipher _storedWriteKey;
        private RecordHandler _recordHandler;
        private ICryptoProvider _cryptoProvider;
        private bool _requiresTicket;
        private bool _abbreviatedHandshake;

        public Server12ConnectionState(SecurePipeConnection secureConnection)
        {
            _secureConnection = secureConnection;
            _secretSchedule = new SecretSchedule12(this);
            _recordHandler = _secureConnection.RecordHandler;
            _cryptoProvider = _secureConnection.Listener.CryptoProvider;
        }

        public CipherSuite CipherSuite { get; set; }
        internal SecurePipeConnection SecureConnection => _secureConnection;
        public IKeyExchange KeyExchange { get; internal set; }
        public IHash HandshakeHash => _handshakeHash;
        public TlsVersion RecordVersion => TlsVersion.Tls12;
        public AeadBulkCipher ReadKey => _readKey;
        public AeadBulkCipher WriteKey => _writeKey;
        public bool HandshakeComplete => _state == HandshakeState.HandshakeCompleted;

        public void ChangeCipherSpec()
        {
            if (_state != HandshakeState.WaitingForChangeCipherSpec)
            {
                Alerts.AlertException.ThrowUnexpectedMessage(RecordType.ChangeCipherSpec);
            }
            (_readKey, _storedWriteKey) = _secretSchedule.GenerateKeys();
            _state = HandshakeState.WaitingForClientFinished;
        }

        public async Task HandleClientHello(ClientHelloParser clientHello)
        {
            _secretSchedule.SetClientRandom(clientHello.ClientRandom);
            CipherSuite = _cryptoProvider.CipherSuites.GetCipherSuite(TlsVersion.Tls12, clientHello.CipherSuites);
            _certificate = _secureConnection.Listener.CertificateList.GetCertificate(null, CipherSuite.CertificateType.Value);
            _handshakeHash = _cryptoProvider.HashProvider.GetHash(CipherSuite.HashType);
            _handshakeHash.HashData(clientHello.OriginalMessage);
            ParseExtensions(ref clientHello);
            if (_abbreviatedHandshake)
            {
                var writer = _secureConnection.HandshakeOutput.Writer.Alloc();
                WriteServerHello(ref writer, clientHello.SessionId);
                _secretSchedule.WriteSessionTicket(ref writer);
                await writer.FlushAsync();
                await _recordHandler.WriteRecords(_secureConnection.HandshakeOutput.Reader, RecordType.Handshake, false);
                await WriteChangeCipherSpec();
                _writeKey = _storedWriteKey;
                writer = _secureConnection.HandshakeOutput.Writer.Alloc();
                _secretSchedule.GenerateAndWriteServerVerify(ref writer);
                await writer.FlushAsync();
                await _recordHandler.WriteRecords(_secureConnection.HandshakeOutput.Reader, RecordType.Handshake, true);
                _state = HandshakeState.WaitingForClientFinishedAbbreviated;
            }
            else
            {
                if (KeyExchange == null)
                {
                    KeyExchange = _cryptoProvider.KeyExchangeProvider.GetKeyExchange(CipherSuite.KeyExchange, default(Span<byte>));
                }
                var writer = _secureConnection.HandshakeOutput.Writer.Alloc();
                SendFirstFlight(ref writer);
                await writer.FlushAsync();
                _state = HandshakeState.WaitingForClientKeyExchange;
                await _recordHandler.WriteRecords(_secureConnection.HandshakeOutput.Reader, RecordType.Handshake, true);
            }
            var ignore = ReadingLoop();
        }

        private async Task ReadingLoop()
        {
            while (true)
            {
                var reader = await _secureConnection.HandshakeInput.Reader.ReadAsync();
                var buffer = reader.Buffer;
                WritableBuffer writer;
                try
                {
                    while (HandshakeFraming.ReadHandshakeFrame(ref buffer, out ReadableBuffer messageBuffer, out HandshakeType messageType))
                    {
                        Span<byte> span;
                        switch (messageType)
                        {
                            case HandshakeType.client_key_exchange when _state == HandshakeState.WaitingForClientKeyExchange:
                                span = messageBuffer.ToSpan();
                                _handshakeHash.HashData(span);
                                span = span.Slice(HandshakeFraming.HeaderSize);
                                KeyExchange.SetPeerKey(span, _certificate, _signatureScheme);
                                _secretSchedule.GenerateMasterSecret();
                                _state = HandshakeState.WaitingForChangeCipherSpec;
                                break;
                            case HandshakeType.finished when _state == HandshakeState.WaitingForClientFinished:
                                span = messageBuffer.ToSpan();
                                if (_secretSchedule.GenerateAndCompareClientVerify(span))
                                {
                                    _state = HandshakeState.HandshakeCompleted;
                                }
                                if (_requiresTicket)
                                {
                                    writer = _secureConnection.HandshakeOutput.Writer.Alloc();
                                    _secretSchedule.WriteSessionTicket(ref writer);
                                    await writer.FlushAsync();
                                    await _recordHandler.WriteRecords(_secureConnection.HandshakeOutput.Reader, RecordType.Handshake, false);
                                }
                                await WriteChangeCipherSpec();
                                _writeKey = _storedWriteKey;
                                writer = _secureConnection.HandshakeOutput.Writer.Alloc();
                                _secretSchedule.GenerateAndWriteServerVerify(ref writer);
                                await writer.FlushAsync();
                                await _recordHandler.WriteRecords(_secureConnection.HandshakeOutput.Reader, RecordType.Handshake, true);
                                break;

                            case HandshakeType.finished when _state == HandshakeState.WaitingForClientFinishedAbbreviated:
                                break;
                            default:
                                throw new NotImplementedException();
                        }
                    }
                }
                finally
                {
                    _secureConnection.HandshakeInput.Reader.Advance(buffer.Start, buffer.End);
                }
            }
        }

        private async Task WriteChangeCipherSpec()
        {
            var writer = _secureConnection.HandshakeOutput.Writer.Alloc();
            writer.WriteBigEndian<byte>(1);
            await writer.FlushAsync();
            await _recordHandler.WriteRecords(_secureConnection.HandshakeOutput.Reader, RecordType.ChangeCipherSpec, false);
        }

        private void ParseExtensions(ref ClientHelloParser clientHello)
        {
            foreach (var (extensionType, buffer) in clientHello.Extensions)
            {
                switch (extensionType)
                {
                    case ExtensionType.application_layer_protocol_negotiation:
                        _negotiatedAlpn = _secureConnection.Listener.AlpnProvider.ProcessExtension(buffer);
                        break;
                    case ExtensionType.supported_groups:
                        KeyExchange = _cryptoProvider.KeyExchangeProvider.GetKeyExchange(CipherSuite.KeyExchange, buffer);
                        break;
                    case ExtensionType.signature_algorithms:
                        _signatureScheme = _certificate.SelectAlgorithm(buffer);
                        break;
                    case ExtensionType.renegotiation_info:
                        _secureConnection.Listener.SecureRenegotiationProvider.ProcessExtension(buffer);
                        _secureRenegotiation = true;
                        break;
                    case ExtensionType.SessionTicket:
                        ProcessSessionTicket(buffer);
                        break;
                    case ExtensionType.server_name:
                        break;
                    default:
                        throw new NotImplementedException();
                }
            }
        }

        private void ProcessSessionTicket(Span<byte> buffer)
        {
            _requiresTicket = true;
            if (buffer.Length == 0 || !_secretSchedule.ReadSessionTicket(buffer))
            {
                return;
            }
            _abbreviatedHandshake = true;
        }

        public void Dispose()
        {
            _handshakeHash?.Dispose();
            _handshakeHash = null;
            KeyExchange?.Dispose();
            KeyExchange = null;
        }

        ~Server12ConnectionState()
        {
            Dispose();
        }
    }
}
