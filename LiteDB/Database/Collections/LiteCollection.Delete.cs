﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <summary>
        /// Remove an document in collection using Document Id - returns false if not found document
        /// </summary>
        public virtual bool Delete(object id)
        {
            // start transaction
            this.Database.Transaction.Begin();

            try
            {
                var col = this.GetCollectionPage(false);

                // if collection not exists, document do not exists too
                if (col == null)
                {
                    this.Database.Transaction.Abort();
                    return false;
                }

                // find indexNode using PK index
                var node = this.Database.Indexer.FindOne(col.PK, id);

                // if not found, abort transaction and returns false
                if (node == null)
                {
                    this.Database.Transaction.Abort();
                    return false;
                }

                this.Remove(col, node);

                this.Database.Transaction.Commit();

                return true;
            }
            catch (Exception ex)
            {
                this.Database.Transaction.Rollback();
                throw ex;
            }
        }

        /// <summary>
        /// Remove all document based on a Query object. Returns removed document counts
        /// </summary>
        public virtual int Delete(Query query)
        {
            // start transaction
            this.Database.Transaction.Begin();

            try
            {
                var col = this.GetCollectionPage(false);

                // no collection, no document - abort trans
                if (col == null)
                {
                    this.Database.Transaction.Abort();
                    return 0;
                }

                var count = 0;

                // find nodes
                var nodes = query.Run(this.Database, col);

                foreach (var node in nodes)
                {
                    this.Remove(col, node);
                    count++;
                }

                // no deletes, just abort transaction (no writes)
                if (count == 0)
                {
                    this.Database.Transaction.Abort();
                    return 0;
                }

                this.Database.Transaction.Commit();

                return count;
            }
            catch (Exception ex)
            {
                this.Database.Transaction.Rollback();
                throw ex;
            }
        }

        /// <summary>
        /// Remove all document based on a LINQ query. Returns removed document counts
        /// </summary>
        public virtual int Delete(Expression<Func<T, bool>> predicate)
        {
            return this.Delete(QueryVisitor.Visit(predicate));
        }

        /// <summary>
        /// Remove all documents on this collection. Returns removed document counts
        /// </summary>
        /// <returns></returns>
        public virtual int DeleteAll()
        {
            return this.Delete(Query.All());
        }

        internal virtual void Remove(CollectionPage col, IndexNode node)
        {
            // read dataBlock 
            var dataBlock = this.Database.Data.Read(node.DataBlock, false);

            // lets remove all indexes that point to this in dataBlock
            for (byte i = 0; i < col.Indexes.Length; i++)
            {
                var index = col.Indexes[i];

                if (!index.IsEmpty)
                {
                    this.Database.Indexer.Delete(index, dataBlock.IndexRef[i]);
                }
            }

            // remove object data
            this.Database.Data.Delete(col, node.DataBlock);
        }
    }
}