﻿using BitSharp.Common;
using BitSharp.Core.Domain;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace BitSharp.Core
{
    public class BlockProvider : IDisposable
    {
        private readonly ConcurrentDictionary<string, Block> blocks;
        private readonly Dictionary<int, string> heightNames;
        private readonly Dictionary<UInt256, string> hashNames;
        private readonly ZipArchive zip;

        private bool isDisposed;

        public BlockProvider(string resourceName)
        {
            this.blocks = new ConcurrentDictionary<string, Block>();
            this.heightNames = new Dictionary<int, string>();
            this.hashNames = new Dictionary<UInt256, string>();

            var assembly = Assembly.GetCallingAssembly();
            var stream = assembly.GetManifestResourceStream(resourceName);

            this.zip = new ZipArchive(stream);

            foreach (var entry in zip.Entries)
            {
                var chunks = Path.GetFileNameWithoutExtension(entry.Name).Split('_');
                var blockHeight = int.Parse(chunks[0]);
                var blockHash = UInt256.ParseHex(chunks[1]);

                heightNames.Add(blockHeight, entry.Name);
                hashNames.Add(blockHash, entry.Name);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!isDisposed && disposing)
            {
                this.zip.Dispose();

                isDisposed = true;
            }
        }

        public int Count => heightNames.Count;

        public IEnumerable<Block> ReadBlocks()
        {
            for (var height = 0; height < Count; height++)
                yield return GetBlock(height);
        }

        public Block GetBlock(int height)
        {
            var name = heightNames[height];
            if (name == null)
                return null;

            return GetEntry(name);
        }

        public Block GetBlock(UInt256 hash)
        {
            var name = hashNames[hash];
            if (name == null)
                return null;

            return GetEntry(name);
        }

        private Block GetEntry(string name)
        {
            Block block;
            if (blocks.TryGetValue(name, out block))
                return block;

            var entry = zip.GetEntry(name);
            if (entry == null)
                return null;

            using (var blockStream = entry.Open())
            using (var blockReader = new BinaryReader(blockStream))
            {
                block = DataDecoder.DecodeBlock(blockReader);
            }

            blocks[name] = block;

            return block;
        }
    }
}
