﻿using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using Leto.Tls13.State;

namespace Leto.Tls13.Sessions
{
    public class ResumptionProvider
    {
        private ResumptionKey[] _keyset;
        private int _historySize;
        private int _currentIndex;
        private int _currentInsertPoint;
        private CryptoProvider _provider;

        public ResumptionProvider(int historySize, CryptoProvider provider)
        {
            _historySize = historySize;
            _keyset = new ResumptionKey[_historySize];
        }
        
        public void AddNewKey(DateTime keyExpiry, DateTime keyActivated, ResumptionKey newKey)
        {
            lock (_keyset)
            {
                _currentInsertPoint++;
                _keyset[_currentInsertPoint % _historySize] = newKey;
                if(keyActivated < DateTime.UtcNow)
                {
                    _currentIndex = _currentInsertPoint;
                }
            }
        }

        public void GenerateSessionTicket(ref WritableBuffer writer, ConnectionState state)
        {
            var key = _keyset[_currentIndex];
            writer.WriteBigEndian(123456);
        }

        public unsafe void GenerateResumptionKey()
        {
            var timestamp = DateTime.UtcNow.ToBinary();
            var code = stackalloc long[2];
            _provider.FillWithRandom(code, 128);
            code[0] = code[0] ^ timestamp;
            var key = new byte[16];
            var nounceBase = new byte[12];
            _provider.FillWithRandom(key);
            _provider.FillWithRandom(nounceBase);

            var newKey = new ResumptionKey(code[0], code[1], key, nounceBase);

        }
    }
}