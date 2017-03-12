﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Leto.Interop
{
    internal static partial class LibCrypto
    {
        [DllImport(Libraries.LibCrypto, CallingConvention = CallingConvention.Cdecl, EntryPoint = nameof(EVP_MD_CTX_copy_ex))]
        private static extern int EVP_MD_CTX_copy_ex_internal(EVP_MD_CTX copy, EVP_MD_CTX original);

        internal static EVP_MD_CTX EVP_MD_CTX_copy_ex(EVP_MD_CTX original)
        {
            var copy = EVP_MD_CTX_new();
            var result = EVP_MD_CTX_copy_ex_internal(copy, original);
            ThrowOnErrorReturnCode(result);
            return copy;
        }
    }
}
