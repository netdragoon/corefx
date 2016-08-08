﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Win32.SafeHandles;

namespace Internal.Cryptography.Pal.OpenSsl
{
    internal static class OpenSslHelpers
    {
        public static string GetContentType(this SafeCmsHandle cmsHandle)
        {
            using (SafeSharedAsn1ObjectHandle oidAsn1 = Interop.Crypto.CmsGetMessageContentType(cmsHandle))
            {
                Interop.Crypto.CheckValidOpenSslHandle(oidAsn1);
                return Interop.Crypto.GetOidValue(oidAsn1);
            }
        }

        public static ContentInfo GetEmbeddedContent(this SafeCmsHandle cmsHandle)
        {
            string oid;
            Oid contentType;
            byte[] content;

            using (SafeSharedAsn1ObjectHandle oidAsn1 = Interop.Crypto.CmsGetEmbeddedContentType(cmsHandle))
            {
                Interop.Crypto.CheckValidOpenSslHandle(oidAsn1);
                oid = Interop.Crypto.GetOidValue(oidAsn1);
            }

            contentType = new Oid(oid);

            using (SafeSharedAsn1OctetStringHandle encodedContent = Interop.Crypto.CmsGetEmbeddedContent(cmsHandle))
            {
                // encodedContent can be a null pointer if there is no content. In this case the content should be set
                // to an empty byte array. 
                if (encodedContent.IsInvalid)
                    content = Array.Empty<byte>();
                else
                    content = Interop.Crypto.GetAsn1StringBytes(encodedContent);
            }

            return new ContentInfo(contentType, content);
        }

        public static X509Certificate2Collection GetOriginatorCerts(this SafeCmsHandle cmsHandle)
        {
            X509Certificate2Collection origCertCollection = new X509Certificate2Collection();

            using (SafeX509StackHandle origCertsPtr = Interop.Crypto.CmsGetOriginatorCerts(cmsHandle))
            {
                // origCertsPtr might be a nullptr as originator certs are optional, but in this case,
                // GetX509StackFieldCount will just return 0 and behavior will be as expected. 
                int certCount = Interop.Crypto.GetX509StackFieldCount(origCertsPtr);
                for (int i = 0; i < certCount; i++)
                {
                    IntPtr certRef = Interop.Crypto.GetX509StackField(origCertsPtr, i);
                    Interop.Crypto.CheckValidOpenSslHandle(certRef);
                    X509Certificate2 copyOfCert = new X509Certificate2(certRef);
                    origCertCollection.Add(copyOfCert);
                }
            }

            return origCertCollection;
        }

        public static AlgorithmIdentifier ReadAlgoIdFromEncryptedContentInfo(this DerSequenceReader encodedCms)
        {
            DerSequenceReader encryptedContentInfo = encodedCms.ReadSequence();
            // EncryptedContentInfo ::= SEQUENCE {
            //     contentType ContentType,
            //     contentEncryptionAlgorithm ContentEncryptionAlgorithmIdentifier,
            //     encryptedContent[0] IMPLICIT EncryptedContent OPTIONAL }

            // Skip the content type
            Debug.Assert(
                encryptedContentInfo.PeekTag() == (byte)DerSequenceReader.DerTag.ObjectIdentifier,
                "Expected to skip an OID while reading EncryptedContentInfo");
            encryptedContentInfo.SkipValue();

            byte[] contentEncryptionAlgorithmIdentifier = encryptedContentInfo.ReadNextEncodedValue();

            DerSequenceReader encryptionAlgorithmReader = new DerSequenceReader(contentEncryptionAlgorithmIdentifier);
            string algoOid = encryptionAlgorithmReader.ReadOidAsString();

            int keyLength = 0;
            switch (algoOid)
            {
                case Oids.Rc2:
                    keyLength = Interop.Crypto.CmsGetAlgorithmKeyLength(
                        contentEncryptionAlgorithmIdentifier, contentEncryptionAlgorithmIdentifier.Length);

                    if (keyLength == -2)
                    {
                        System.Diagnostics.Debug.Fail("Call to the shim recieved unexpected invalid input.");
                        throw new ArgumentNullException();
                    }

                    if (keyLength == -1)
                        throw Interop.Crypto.CreateOpenSslCryptographicException();

                    break;
                    
                case Oids.Rc4:
                    // We should also set this to the right value but OpenSSL throws an exception when trying to set the ASN1 parameters
                    // to extract the key length as documented on issue 10311.
                    break;

                case Oids.Des:
                    keyLength = KeyLengths.Des_64Bit;
                    break;

                case Oids.TripleDesCbc:
                    keyLength = KeyLengths.TripleDes_192Bit;
                    break;
                    // As we commented in the Windows implementation:
                    // All other algorithms are not set by the framework.  Key lengths are not a viable way of
                    // identifying algorithms in the long run so we will not extend this list any further.
            }

            return new AlgorithmIdentifier(new Oid(algoOid), keyLength);
        }

        public static AlgorithmIdentifier ReadAlgoIdentifier(this DerSequenceReader encryptedContentInfo)
        {
            // The encoding for a ContentEncryptionAlgorithmIdentifier is just a sequence of the OID and the parameters,
            // but we just need the OID
            
            DerSequenceReader contentEncryptionAlgorithmIdentifier = encryptedContentInfo.ReadSequence();
            string algoOid = contentEncryptionAlgorithmIdentifier.ReadOidAsString();

            return new AlgorithmIdentifier(new Oid(algoOid), 0);
        }

        public static CryptographicAttributeObjectCollection ReadUnprotectedAttributes(this DerSequenceReader encodedCms)
        {
            var unprotectedAttributesCollection = new CryptographicAttributeObjectCollection();

            // As the unprotected attributes are optional we have to check if there's anything left to read and check if it's
            // tagged as context specific 1
            byte contextImplicit1 = (byte)(DerSequenceReader.ContextSpecificTagFlag | DerSequenceReader.ConstructedFlag | 0x01);

            if (encodedCms.HasData && encodedCms.PeekTag() == contextImplicit1)
            {
                DerSequenceReader encodedAttributesReader = encodedCms.ReadSet();
                while (encodedAttributesReader.HasData)
                {
                    DerSequenceReader attributeReader = encodedAttributesReader.ReadSequence();

                    Oid attributeOid = attributeReader.ReadOid();
                    AsnEncodedDataCollection attributeCollection = new AsnEncodedDataCollection();
                    DerSequenceReader attributeSetReader = attributeReader.ReadSet();
                    
                    while (attributeSetReader.HasData)
                    {
                        byte[] singleEncodedAttribute = attributeSetReader.ReadNextEncodedValue();
                        AsnEncodedData singleAttribute = Helpers.CreateBestPkcs9AttributeObjectAvailable(attributeOid, singleEncodedAttribute);
                        attributeCollection.Add(singleAttribute);
                    }

                    unprotectedAttributesCollection.Add(new CryptographicAttributeObject(attributeOid, attributeCollection));
                }
            }

            return unprotectedAttributesCollection;
        }
    }
}