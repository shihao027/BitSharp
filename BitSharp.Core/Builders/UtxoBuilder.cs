﻿using BitSharp.Common;
using BitSharp.Core.Domain;
using BitSharp.Core.Storage;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks.Dataflow;

namespace BitSharp.Core.Builders
{
    internal class UtxoBuilder
    {
        private static readonly int DUPE_COINBASE_1_HEIGHT = 91722;
        private static readonly UInt256 DUPE_COINBASE_1_HASH = UInt256.ParseHex("e3bf3d07d4b0375638d5f1db5255fe07ba2c4cb067cd81b84ee974b6585fb468");
        private static readonly int DUPE_COINBASE_2_HEIGHT = 91812;
        private static readonly UInt256 DUPE_COINBASE_2_HASH = UInt256.ParseHex("d5d27987d2a3dfc724e359870c6644b40e497bdc0589a033220fe15429d88599");

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public ISourceBlock<ValidatableTx> CalculateUtxo(IChainStateCursor chainStateCursor, Chain chain, ISourceBlock<DecodedBlockTx> blockTxes, CancellationToken cancelToken = default(CancellationToken))
        {
            var chainedHeader = chain.LastBlock;
            var blockSpentTxes = new BlockSpentTxesBuilder();

            var utxoCalculator = new TransformBlock<DecodedBlockTx, ValidatableTx>(
                blockTx =>
                {
                    var tx = blockTx.Transaction;
                    var txIndex = blockTx.Index;

                    var prevTxOutputs = ImmutableArray.CreateBuilder<PrevTxOutput>(!blockTx.IsCoinbase ? tx.Inputs.Length : 0);

                    //TODO apply real coinbase rule
                    // https://github.com/bitcoin/bitcoin/blob/481d89979457d69da07edd99fba451fd42a47f5c/src/core.h#L219
                    if (!blockTx.IsCoinbase)
                    {
                        // spend each of the transaction's inputs in the utxo
                        for (var inputIndex = 0; inputIndex < tx.Inputs.Length; inputIndex++)
                        {
                            var input = tx.Inputs[inputIndex];
                            var prevTxOutput = this.Spend(chainStateCursor, txIndex, tx, inputIndex, input, chainedHeader, blockSpentTxes);

                            prevTxOutputs.Add(prevTxOutput);
                        }
                    }

                    // there exist two duplicate coinbases in the blockchain, which the design assumes to be impossible
                    // ignore the first occurrences of these duplicates so that they do not need to later be deleted from the utxo, an unsupported operation
                    // no other duplicates will occur again, it is now disallowed
                    var isDupeCoinbase = IsDupeCoinbase(chainedHeader, tx);

                    // add transaction's outputs to utxo, except for the genesis block and the duplicate coinbases
                    if (chainedHeader.Height > 0 && !isDupeCoinbase)
                    {
                        // mint the transaction's outputs in the utxo
                        this.Mint(chainStateCursor, tx, txIndex, chainedHeader);

                        // increase unspent output count
                        chainStateCursor.UnspentOutputCount += tx.Outputs.Length;

                        // increment unspent tx count
                        chainStateCursor.UnspentTxCount++;

                        chainStateCursor.TotalTxCount++;
                        chainStateCursor.TotalInputCount += tx.Inputs.Length;
                        chainStateCursor.TotalOutputCount += tx.Outputs.Length;
                    }

                    return new ValidatableTx(blockTx, chainedHeader, prevTxOutputs.MoveToImmutable());
                },
                new ExecutionDataflowBlockOptions { CancellationToken = cancelToken });

            blockTxes.LinkTo(utxoCalculator, new DataflowLinkOptions { PropagateCompletion = true });

            return OnCompleteBlock.Create(utxoCalculator, () =>
                {
                    if (!chainStateCursor.TryAddBlockSpentTxes(chainedHeader.Height, blockSpentTxes.ToImmutable()))
                        throw new ValidationException(chainedHeader.Hash);
                }, cancelToken);
        }

        private void Mint(IChainStateCursor chainStateCursor, Transaction tx, int txIndex, ChainedHeader chainedHeader)
        {
            // add transaction to the utxo
            var unspentTx = new UnspentTx(tx.Hash, chainedHeader.Height, txIndex, tx.Version, tx.IsCoinbase, tx.Outputs.Length, OutputState.Unspent);
            if (!chainStateCursor.TryAddUnspentTx(unspentTx))
            {
                // duplicate transaction
                logger.Warn($"Duplicate transaction at block {chainedHeader.Height:N0}, {chainedHeader.Hash}, coinbase");
                throw new ValidationException(chainedHeader.Hash);
            }

            // add transaction outputs to the utxo
            for (var outputIndex = 0; outputIndex < tx.Outputs.Length; outputIndex++)
            {
                var output = tx.Outputs[outputIndex];
                if (!chainStateCursor.TryAddUnspentTxOutput(new TxOutputKey(tx.Hash, (uint)outputIndex), output))
                    throw new ValidationException(chainedHeader.Hash);
            }
        }

        private PrevTxOutput Spend(IChainStateCursor chainStateCursor, int txIndex, Transaction tx, int inputIndex, TxInput input, ChainedHeader chainedHeader, BlockSpentTxesBuilder blockSpentTxes)
        {
            UnspentTx unspentTx;
            if (!chainStateCursor.TryGetUnspentTx(input.PrevTxOutputKey.TxHash, out unspentTx))
            {
                // output wasn't present in utxo, invalid block
                throw new ValidationException(chainedHeader.Hash);
            }

            var outputIndex = unchecked((int)input.PrevTxOutputKey.TxOutputIndex);

            if (outputIndex < 0 || outputIndex >= unspentTx.OutputStates.Length)
            {
                // output was out of bounds
                throw new ValidationException(chainedHeader.Hash);
            }

            if (unspentTx.OutputStates[outputIndex] == OutputState.Spent)
            {
                // output was already spent
                throw new ValidationException(chainedHeader.Hash);
            }

            // update output states
            unspentTx = unspentTx.SetOutputState(outputIndex, OutputState.Spent);

            // decrement unspent output count
            chainStateCursor.UnspentOutputCount--;

            // update transaction output states in the utxo
            var wasUpdated = chainStateCursor.TryUpdateUnspentTx(unspentTx);
            if (!wasUpdated)
                throw new ValidationException(chainedHeader.Hash);

            // store pruning information for a fully spent transaction
            if (unspentTx.IsFullySpent)
            {
                blockSpentTxes.AddSpentTx(unspentTx.ToSpentTx());

                // decrement unspent tx count
                chainStateCursor.UnspentTxCount--;
            }

            TxOutput txOutput;
            if (!chainStateCursor.TryGetUnspentTxOutput(input.PrevTxOutputKey, out txOutput))
                // output missing
                throw new ValidationException(chainedHeader.Hash);

            return new PrevTxOutput(txOutput, unspentTx);
        }

        //TODO with the rollback information that's now being stored, rollback could be down without needing the block
        public void RollbackUtxo(IChainStateCursor chainStateCursor, Chain chain, ChainedHeader chainedHeader, IEnumerable<BlockTx> blockTxes, ImmutableList<UnmintedTx>.Builder unmintedTxes)
        {
            //TODO don't reverse here, storage should be read in reverse
            foreach (var blockTx in blockTxes.Reverse())
            {
                var tx = blockTx.Decode().Transaction;
                var txIndex = blockTx.Index;

                // remove transaction's outputs from utxo, except for the genesis block and the duplicate coinbases
                var isDupeCoinbase = IsDupeCoinbase(chainedHeader, tx);
                if (chainedHeader.Height > 0 && !isDupeCoinbase)
                {
                    this.Unmint(chainStateCursor, tx, chainedHeader);

                    // decrease unspent output count
                    chainStateCursor.UnspentOutputCount -= tx.Outputs.Length;

                    // decrement unspent tx count
                    chainStateCursor.UnspentTxCount--;

                    chainStateCursor.TotalTxCount--;
                    chainStateCursor.TotalInputCount -= tx.Inputs.Length;
                    chainStateCursor.TotalOutputCount -= tx.Outputs.Length;
                }

                var prevTxOutputs = ImmutableArray.CreateBuilder<PrevTxOutput>(!tx.IsCoinbase ? tx.Inputs.Length : 0);

                if (!tx.IsCoinbase)
                {
                    // remove inputs in reverse order
                    for (var inputIndex = tx.Inputs.Length - 1; inputIndex >= 0; inputIndex--)
                    {
                        var input = tx.Inputs[inputIndex];
                        var prevTxOutput = this.Unspend(chainStateCursor, input, chainedHeader);

                        // store rollback replay information
                        prevTxOutputs.Add(prevTxOutput);
                    }
                }

                // reverse output keys to match original input order, as the inputs were read in reverse here
                prevTxOutputs.Reverse();

                // store rollback replay information
                unmintedTxes.Add(new UnmintedTx(tx.Hash, prevTxOutputs.MoveToImmutable()));
            }
        }

        private void Unmint(IChainStateCursor chainStateCursor, Transaction tx, ChainedHeader chainedHeader)
        {
            // check that transaction exists
            UnspentTx unspentTx;
            if (!chainStateCursor.TryGetUnspentTx(tx.Hash, out unspentTx))
            {
                // missing transaction output
                logger.Warn($"Missing transaction at block {chainedHeader.Height:N0}, {chainedHeader.Hash}, tx {tx.Hash}");
                throw new ValidationException(chainedHeader.Hash);
            }

            //TODO verify blockheight

            // verify all outputs are unspent before unminting
            if (!unspentTx.OutputStates.All(x => x == OutputState.Unspent))
            {
                throw new ValidationException(chainedHeader.Hash);
            }

            // remove the transaction
            if (!chainStateCursor.TryRemoveUnspentTx(tx.Hash))
            {
                throw new ValidationException(chainedHeader.Hash);
            }

            // remove the tx outputs
            for (var outputIndex = 0; outputIndex < tx.Outputs.Length; outputIndex++)
            {
                if (!chainStateCursor.TryRemoveUnspentTxOutput(new TxOutputKey(tx.Hash, (uint)outputIndex)))
                    throw new ValidationException(chainedHeader.Hash);
            }
        }

        private PrevTxOutput Unspend(IChainStateCursor chainStateCursor, TxInput input, ChainedHeader chainedHeader)
        {
            UnspentTx unspentTx;
            if (!chainStateCursor.TryGetUnspentTx(input.PrevTxOutputKey.TxHash, out unspentTx))
            {
                // unable to rollback, the unspent tx has been pruned
                //TODO better exception
                throw new InvalidOperationException();
            }

            // retrieve previous output index
            var outputIndex = unchecked((int)input.PrevTxOutputKey.TxOutputIndex);
            if (outputIndex < 0 || outputIndex >= unspentTx.OutputStates.Length)
                throw new Exception("TODO - corruption");

            // check that output isn't already considered unspent
            if (unspentTx.OutputStates[outputIndex] == OutputState.Unspent)
                throw new ValidationException(chainedHeader.Hash);

            var wasFullySpent = unspentTx.IsFullySpent;

            // mark output as unspent
            unspentTx = unspentTx.SetOutputState(outputIndex, OutputState.Unspent);

            // increment unspent output count
            chainStateCursor.UnspentOutputCount++;

            // update storage
            var wasUpdated = chainStateCursor.TryUpdateUnspentTx(unspentTx);
            if (!wasUpdated)
                throw new ValidationException(chainedHeader.Hash);

            // increment unspent tx count
            if (wasFullySpent)
                chainStateCursor.UnspentTxCount++;

            TxOutput txOutput;
            if (!chainStateCursor.TryGetUnspentTxOutput(input.PrevTxOutputKey, out txOutput))
                // output missing
                throw new ValidationException(chainedHeader.Hash);

            return new PrevTxOutput(txOutput, unspentTx);
        }

        private bool IsDupeCoinbase(ChainedHeader chainedHeader, Transaction tx)
        {
            return ((tx.IsCoinbase && chainedHeader.Height == DUPE_COINBASE_1_HEIGHT && tx.Hash == DUPE_COINBASE_1_HASH)
               || (tx.IsCoinbase && chainedHeader.Height == DUPE_COINBASE_2_HEIGHT && tx.Hash == DUPE_COINBASE_2_HASH));
        }
    }
}