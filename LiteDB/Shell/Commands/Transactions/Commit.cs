﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace LiteDB.Shell.Commands
{
    public class Commit : ILiteCommand
    {
        public bool IsCommand(StringScanner s)
        {
            return s.Scan(@"commit(\s+trans)?$").Length > 0;
        }

        public void Execute(LiteDatabase db, StringScanner s, Display display)
        {
            if (db == null) throw new LiteException("No database");

            db.Commit();
        }
    }
}