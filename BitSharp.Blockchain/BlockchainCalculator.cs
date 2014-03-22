﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BitSharp.Data;
using System.IO;
using BitSharp.Storage;
using System.Globalization;
using System.Collections;

namespace BitSharp.Blockchain
{
    public class BlockchainCalculator
    {
        private readonly IBlockchainRules _rules;
        private readonly ICacheContext _cacheContext;
        private readonly CancellationToken shutdownToken;

        public BlockchainCalculator(IBlockchainRules rules, ICacheContext cacheContext, CancellationToken shutdownToken)
        {
            this._rules = rules;
            this._cacheContext = cacheContext;
            this.shutdownToken = shutdownToken;
        }

        public IBlockchainRules Rules { get { return this._rules; } }

        public ICacheContext CacheContext { get { return this._cacheContext; } }

        public void CalculateBlockchainFromExisting(ChainStateBuilder chainStateBuilder, Func<ChainedBlocks> getTargetChainedBlocks, CancellationToken cancelToken, Action onProgress = null)
        {
            chainStateBuilder.Stats.totalStopwatch.Start();
            chainStateBuilder.Stats.currentRateStopwatch.Start();

            // calculate the new blockchain along the target path
            chainStateBuilder.IsConsistent = true;
            foreach (var pathElement in BlockAndInputsLookAhead(chainStateBuilder.ChainedBlocks.NavigateTowards(getTargetChainedBlocks), maxLookAhead: 100))
            {
                chainStateBuilder.IsConsistent = false;

                // cooperative loop
                if (this.shutdownToken.IsCancellationRequested)
                    break;
                if (cancelToken.IsCancellationRequested)
                    break;

                // get block and metadata for next link in blockchain
                var direction = pathElement.Item1;
                var chainedBlock = pathElement.Item2;
                var block = pathElement.Item3;
                var prevInputTxes = pathElement.Item4;

                if (direction < 0)
                {
                    RollbackUtxo(chainStateBuilder, block);

                    chainStateBuilder.ChainedBlocks.RemoveBlock(chainedBlock);
                }
                else if (direction > 0)
                {
                    // calculate the new block utxo, double spends will be checked for
                    long txCount = 0, inputCount = 0;
                    new MethodTimer(false).Time("CalculateUtxo", () =>
                        CalculateUtxo(chainedBlock.Height, block, chainStateBuilder.Utxo, out txCount, out inputCount));

                    chainStateBuilder.ChainedBlocks.AddBlock(chainedBlock);

                    // validate the block
                    // validation utxo includes all transactions added in the same block, any double spends will have failed the block above
                    chainStateBuilder.Stats.validateStopwatch.Start();
                    try
                    {
                        new MethodTimer(false).Time("ValidateBlock", () =>
                            this.Rules.ValidateBlock(block, chainStateBuilder, prevInputTxes));
                    }
                    finally
                    {
                        chainStateBuilder.Stats.validateStopwatch.Stop();
                    }

                    // flush utxo progress
                    //chainStateBuilder.Utxo.Flush();

                    // create the next link in the new blockchain
                    if (onProgress != null)
                        onProgress();

                    // blockchain processing statistics
                    chainStateBuilder.Stats.currentBlockCount++;
                    chainStateBuilder.Stats.currentTxCount += txCount;
                    chainStateBuilder.Stats.currentInputCount += inputCount;
                    chainStateBuilder.Stats.totalTxCount += txCount;
                    chainStateBuilder.Stats.totalInputCount += inputCount;

                    var txInterval = 100.THOUSAND();
                    if (
                        chainStateBuilder.ChainedBlocks.Height % 10.THOUSAND() == 0
                        || (chainStateBuilder.Stats.totalTxCount % txInterval < (chainStateBuilder.Stats.totalTxCount - txCount) % txInterval || txCount >= txInterval))
                    {
                        LogBlockchainProgress(chainStateBuilder);

                        chainStateBuilder.Stats.currentBlockCount = 0;
                        chainStateBuilder.Stats.currentTxCount = 0;
                        chainStateBuilder.Stats.currentInputCount = 0;
                        chainStateBuilder.Stats.currentRateStopwatch.Reset();
                        chainStateBuilder.Stats.currentRateStopwatch.Start();
                    }
                }
                else
                    throw new InvalidOperationException();

                chainStateBuilder.IsConsistent = true;
            }

            if (onProgress != null)
                onProgress();

            LogBlockchainProgress(chainStateBuilder);
            chainStateBuilder.Stats.totalStopwatch.Stop();
            chainStateBuilder.Stats.currentRateStopwatch.Stop();
        }

        private void LogBlockchainProgress(ChainStateBuilder chainStateBuilder)
        {
            var currentBlockRate = (float)chainStateBuilder.Stats.currentBlockCount / chainStateBuilder.Stats.currentRateStopwatch.ElapsedSecondsFloat();
            var currentTxRate = (float)chainStateBuilder.Stats.currentTxCount / chainStateBuilder.Stats.currentRateStopwatch.ElapsedSecondsFloat();
            var currentInputRate = (float)chainStateBuilder.Stats.currentInputCount / chainStateBuilder.Stats.currentRateStopwatch.ElapsedSecondsFloat();

            Debug.WriteLine(
                string.Join("\n",
                    new string('-', 80),
                    "Height: {0,10} | Duration: {1} hh:mm:ss | Validation: {2} hh:mm:ss | Blocks/s: {3,7} | Tx/s: {4,7} | Inputs/s: {5,7} | Total Tx: {6,7} | Total Inputs: {7,7} | Utxo Size: {8,7}",
                    "GC Memory:      {9,10:#,##0.00} MB",
                    "Process Memory: {10,10:#,##0.00} MB",
                    new string('-', 80)
                )
                .Format2
                (
                /*0*/ chainStateBuilder.ChainedBlocks.Height.ToString("#,##0"),
                /*1*/ chainStateBuilder.Stats.totalStopwatch.Elapsed.ToString(@"hh\:mm\:ss"),
                /*2*/ chainStateBuilder.Stats.validateStopwatch.Elapsed.ToString(@"hh\:mm\:ss"),
                /*3*/ currentBlockRate.ToString("#,##0"),
                /*4*/ currentTxRate.ToString("#,##0"),
                /*5*/ currentInputRate.ToString("#,##0"),
                /*6*/ chainStateBuilder.Stats.totalTxCount.ToString("#,##0"),
                /*7*/ chainStateBuilder.Stats.totalInputCount.ToString("#,##0"),
                /*8*/ chainStateBuilder.Utxo.Count.ToString("#,##0"),
                /*9*/ (float)GC.GetTotalMemory(false) / 1.MILLION(),
                /*10*/ (float)Process.GetCurrentProcess().PrivateMemorySize64 / 1.MILLION()
                ));
        }

        private void CalculateUtxo(int blockHeight, Block block, UtxoBuilder utxoBuilder, out long txCount, out long inputCount)
        {
            txCount = 1;
            inputCount = 0;

            // don't include genesis block coinbase in utxo
            if (blockHeight > 0)
            {
                //TODO apply real coinbase rule
                // https://github.com/bitcoin/bitcoin/blob/481d89979457d69da07edd99fba451fd42a47f5c/src/core.h#L219
                var coinbaseTx = block.Transactions[0];

                utxoBuilder.Mint(coinbaseTx, blockHeight);
            }

            // check for double spends
            for (var txIndex = 1; txIndex < block.Transactions.Count; txIndex++)
            {
                var tx = block.Transactions[txIndex];
                txCount++;

                for (var inputIndex = 0; inputIndex < tx.Inputs.Count; inputIndex++)
                {
                    var input = tx.Inputs[inputIndex];
                    inputCount++;

                    utxoBuilder.Spend(input);
                }

                utxoBuilder.Mint(tx, blockHeight);
            }
        }

        private void RollbackUtxo(ChainStateBuilder chainStateBuilder, Block block)
        {
            var blockHeight = chainStateBuilder.ChainedBlocks.Height;
            var utxoBuilder = chainStateBuilder.Utxo;

            for (var txIndex = block.Transactions.Count - 1; txIndex >= 1; txIndex--)
            {
                var tx = block.Transactions[txIndex];

                // remove outputs
                utxoBuilder.Unmint(tx, blockHeight);

                // remove inputs in reverse order
                for (var inputIndex = tx.Inputs.Count - 1; inputIndex >= 0; inputIndex--)
                {
                    var input = tx.Inputs[inputIndex];
                    utxoBuilder.Unspend(input);
                }
            }

            // remove coinbase outputs
            var coinbaseTx = block.Transactions[0];
            utxoBuilder.Unmint(coinbaseTx, blockHeight);
        }

        public void RevalidateBlockchain(ChainedBlocks blockchain, Block genesisBlock)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            try
            {
                //TODO delete corrupted data? could get stuck in a fail-loop on the winning chain otherwise

                // verify blockchain has blocks
                if (blockchain.BlockList.Count == 0)
                    throw new ValidationException();

                // verify genesis block hash
                if (blockchain.BlockList[0].BlockHash != genesisBlock.Hash)
                    throw new ValidationException();

                // get genesis block header
                var chainGenesisBlockHeader = this.CacheContext.BlockHeaderCache[blockchain.BlockList[0].BlockHash];

                // verify genesis block header
                if (
                    genesisBlock.Header.Version != chainGenesisBlockHeader.Version
                    || genesisBlock.Header.PreviousBlock != chainGenesisBlockHeader.PreviousBlock
                    || genesisBlock.Header.MerkleRoot != chainGenesisBlockHeader.MerkleRoot
                    || genesisBlock.Header.Time != chainGenesisBlockHeader.Time
                    || genesisBlock.Header.Bits != chainGenesisBlockHeader.Bits
                    || genesisBlock.Header.Nonce != chainGenesisBlockHeader.Nonce
                    || genesisBlock.Hash != chainGenesisBlockHeader.Hash
                    || genesisBlock.Hash != CalculateHash(chainGenesisBlockHeader))
                {
                    throw new ValidationException();
                }

                // setup expected previous block hash value to verify each chain actually does link
                var expectedPreviousBlockHash = genesisBlock.Header.PreviousBlock;
                for (var height = 0; height < blockchain.BlockList.Count; height++)
                {
                    // cooperative loop
                    this.shutdownToken.ThrowIfCancellationRequested();

                    // get the current link in the chain
                    var chainedBlock = blockchain.BlockList[height];

                    // verify height
                    if (chainedBlock.Height != height)
                        throw new ValidationException();

                    // verify blockchain linking
                    if (chainedBlock.PreviousBlockHash != expectedPreviousBlockHash)
                        throw new ValidationException();

                    // verify block exists
                    var blockHeader = this.CacheContext.BlockHeaderCache[chainedBlock.BlockHash];

                    // verify block metadata matches header values
                    if (blockHeader.PreviousBlock != chainedBlock.PreviousBlockHash)
                        throw new ValidationException();

                    // verify block header hash
                    if (CalculateHash(blockHeader) != chainedBlock.BlockHash)
                        throw new ValidationException();

                    // next block metadata should have the current metadata's hash as its previous hash value
                    expectedPreviousBlockHash = chainedBlock.BlockHash;
                }

                // all validation passed
            }
            finally
            {
                stopwatch.Stop();
                Debug.WriteLine("Blockchain revalidation: {0:#,##0.000000}s".Format2(stopwatch.ElapsedSecondsFloat()));
            }
        }

        public IEnumerable<Tuple<int, ChainedBlock, Block, ImmutableDictionary<UInt256, Transaction>>> BlockAndInputsLookAhead(IEnumerable<Tuple<int, ChainedBlock>> chainedBlocks, int maxLookAhead)
        {
            return chainedBlocks
                .Select(
                    chainedBlockTuple =>
                    {
                        var chainedBlockDirection = chainedBlockTuple.Item1;
                        var chainedBlock = chainedBlockTuple.Item2;

                        var block = new MethodTimer(false).Time("GetBlock", () =>
                            this.CacheContext.BlockView[chainedBlock.BlockHash]);

                        var prevInputTxes = ImmutableDictionary.CreateBuilder<UInt256, Transaction>();
                        new MethodTimer(false).Time("GetPrevInputTxes", () =>
                        {
                            foreach (var prevInput in block.Transactions.Skip(1).SelectMany(x => x.Inputs))
                            {
                                var prevInputTxHash = prevInput.PreviousTxOutputKey.TxHash;
                                if (!prevInputTxes.ContainsKey(prevInputTxHash))
                                    prevInputTxes.Add(prevInputTxHash, this.CacheContext.TransactionCache[prevInputTxHash]);
                            }
                        });

                        return Tuple.Create(chainedBlockDirection, chainedBlock, block, prevInputTxes.ToImmutable());
                    })
                .LookAhead(maxLookAhead, this.shutdownToken);
        }

        private UInt256 CalculateHash(BlockHeader blockHeader)
        {
            return new UInt256(Crypto.DoubleSHA256(DataCalculator.EncodeBlockHeader(blockHeader)));
        }
    }
}
