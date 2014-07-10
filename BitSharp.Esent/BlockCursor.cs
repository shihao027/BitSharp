﻿using Microsoft.Isam.Esent.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Esent
{
    internal class BlockCursor : IDisposable
    {
        public readonly string jetDatabase;
        public readonly Instance jetInstance;

        public readonly Session jetSession;
        public readonly JET_DBID blockDbId;

        public readonly JET_TABLEID globalTableId;
        public readonly JET_COLUMNID flushColumnId;

        public readonly JET_TABLEID blockHeadersTableId;
        public readonly JET_COLUMNID blockHeaderHashColumnId;
        public readonly JET_COLUMNID blockHeaderPreviousHashColumnId;
        public readonly JET_COLUMNID blockHeaderHeightColumnId;
        public readonly JET_COLUMNID blockHeaderTotalWorkColumnId;
        public readonly JET_COLUMNID blockHeaderValidColumnId;
        public readonly JET_COLUMNID blockHeaderBytesColumnId;

        public BlockCursor(string jetDatabase, Instance jetInstance)
        {
            this.jetDatabase = jetDatabase;
            this.jetInstance = jetInstance;

            this.OpenCursor(this.jetDatabase, this.jetInstance, false /*readOnly*/,
                out this.jetSession,
                out this.blockDbId,
                out this.globalTableId,
                    out this.flushColumnId,
                out this.blockHeadersTableId,
                    out this.blockHeaderHashColumnId,
                    out this.blockHeaderPreviousHashColumnId,
                    out this.blockHeaderHeightColumnId,
                    out this.blockHeaderTotalWorkColumnId,
                    out this.blockHeaderBytesColumnId);
        }

        public void Dispose()
        {
            this.jetSession.Dispose();
        }

        private void OpenCursor(string jetDatabase, Instance jetInstance, bool readOnly,
            out Session jetSession,
            out JET_DBID blockDbId,
            out JET_TABLEID globalTableId,
            out JET_COLUMNID flushColumnId,
            out JET_TABLEID blockHeadersTableId,
            out JET_COLUMNID blockHeaderHashColumnId,
            out JET_COLUMNID blockHeaderPreviousHashColumnId,
            out JET_COLUMNID blockHeaderHeightColumnId,
            out JET_COLUMNID blockHeaderTotalWorkColumnId,
            out JET_COLUMNID blockHeaderBytesColumnId)
        {
            jetSession = new Session(jetInstance);
            try
            {
                Api.JetAttachDatabase(jetSession, jetDatabase, readOnly ? AttachDatabaseGrbit.ReadOnly : AttachDatabaseGrbit.None);
                Api.JetOpenDatabase(jetSession, jetDatabase, "", out blockDbId, readOnly ? OpenDatabaseGrbit.ReadOnly : OpenDatabaseGrbit.None);

                Api.JetOpenTable(jetSession, blockDbId, "global", null, 0, readOnly ? OpenTableGrbit.ReadOnly : OpenTableGrbit.None, out globalTableId);
                flushColumnId = Api.GetTableColumnid(jetSession, globalTableId, "Flush");

                Api.JetOpenTable(jetSession, blockDbId, "BlockHeaders", null, 0, readOnly ? OpenTableGrbit.ReadOnly : OpenTableGrbit.None, out blockHeadersTableId);
                blockHeaderHashColumnId = Api.GetTableColumnid(jetSession, blockHeadersTableId, "BlockHash");
                blockHeaderPreviousHashColumnId = Api.GetTableColumnid(jetSession, blockHeadersTableId, "PreviousBlockHash");
                blockHeaderHeightColumnId = Api.GetTableColumnid(jetSession, blockHeadersTableId, "Height");
                blockHeaderTotalWorkColumnId = Api.GetTableColumnid(jetSession, blockHeadersTableId, "TotalWork");
                blockHeaderBytesColumnId = Api.GetTableColumnid(jetSession, blockHeadersTableId, "BlockHeaderBytes");
            }
            catch (Exception)
            {
                jetSession.Dispose();
                throw;
            }
        }
    }
}
