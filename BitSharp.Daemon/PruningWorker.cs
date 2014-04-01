﻿using BitSharp.Blockchain;
using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Data;
using BitSharp.Storage;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitSharp.Daemon
{
    public class PruningWorker : Worker
    {
        private readonly IBlockchainRules rules;
        private readonly Func<ChainState> getChainState;
        private readonly BlockTxHashesCache blockTxHashesCache;
        private readonly TransactionCache transactionCache;
        private readonly BlockRollbackCache blockRollbackCache;
        private readonly SpentOutputsCache spentOutputsCache;

        public PruningWorker(WorkerConfig workerConfig, Func<ChainState> getChainState, Logger logger, IBlockchainRules rules, BlockTxHashesCache blockTxHashesCache, TransactionCache transactionCache, BlockRollbackCache blockRollbackCache, SpentOutputsCache spentOutputsCache)
            : base("PruningWorker", workerConfig.initialNotify, workerConfig.minIdleTime, workerConfig.maxIdleTime, logger)
        {
            this.getChainState = getChainState;
            this.rules = rules;
            this.blockTxHashesCache = blockTxHashesCache;
            this.transactionCache = transactionCache;
            this.blockRollbackCache = blockRollbackCache;
            this.spentOutputsCache = spentOutputsCache;

            this.Mode = PruningMode.Full;
        }

        public PruningMode Mode { get; set; }

        protected override void WorkAction()
        {
            var chainState = this.getChainState();
            if (chainState == null)
                return;

            var blocksPerDay = 144;
            var pruneBuffer = blocksPerDay * 7;

            switch (this.Mode)
            {
                case PruningMode.PreserveUnspentTranscations:
                    for (var i = 0; i < chainState.Chain.Blocks.Count - pruneBuffer; i++)
                    {
                        var block = chainState.Chain.Blocks[i];

                        IImmutableList<KeyValuePair<UInt256, UInt256>> blockRollbackInformation;
                        if (this.blockRollbackCache.TryGetValue(block.BlockHash, out blockRollbackInformation))
                        {
                            foreach (var keyPair in blockRollbackInformation)
                                this.transactionCache.TryRemove(keyPair.Key);
                        }
                    }
                    break;

                case PruningMode.Full:
                    for (var i = 0; i < chainState.Chain.Blocks.Count /*- pruneBuffer*/; i++)
                    {
                        var block = chainState.Chain.Blocks[i];

                        IImmutableList<UInt256> blockTxHashes;
                        if (this.blockTxHashesCache.TryGetValue(block.BlockHash, out blockTxHashes))
                        {
                            foreach (var txHash in blockTxHashes)
                                this.transactionCache.TryRemove(txHash);
                        }
                    }
                    break;
            }

            this.transactionCache.Flush();

            for (var i = 0; i < chainState.Chain.Blocks.Count - pruneBuffer; i++)
            {
                var block = chainState.Chain.Blocks[i];

                this.blockTxHashesCache.TryRemove(block.BlockHash);
                this.blockRollbackCache.TryRemove(block.BlockHash);
                this.spentOutputsCache.TryRemove(block.BlockHash);
            }

            this.blockTxHashesCache.Flush();
            this.blockRollbackCache.Flush();
            this.spentOutputsCache.Flush();
        }
    }

    public enum PruningMode
    {
        PreserveUnspentTranscations,
        Full
    }
}
