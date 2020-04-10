using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FastTunnel.Core.Helper
{
    public static class FileHelper
    {
        public static byte[] GetBytesFromFile(string fileName)
        {
            if (!File.Exists(fileName))
            {
                return null;
            }

            FileInfo fi = new FileInfo(fileName);
            byte[] buff = new byte[fi.Length];

            FileStream fs = fi.OpenRead();
            fs.Read(buff, 0, Convert.ToInt32(fs.Length));
            fs.Close();

            return buff;
        }
    }
}
