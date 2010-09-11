﻿using System;
using System.Text;
using System.Security.Cryptography;
using Mono.Math;

namespace L2Script.Library.Encryption
{

#if INSIDE_CORLIB
	internal
#else
    public
#endif
 class RSA : System.Security.Cryptography.RSA
    {

        private const int defaultKeySize = 1024;

        private bool isCRTpossible = false;
        private bool keyBlinding = true;
        private bool keypairGenerated = false;
        private bool m_disposed = false;

        private BigInteger d;
        private BigInteger p;
        private BigInteger q;
        private BigInteger dp;
        private BigInteger dq;
        private BigInteger qInv;
        private BigInteger n;		// modulus
        private BigInteger e;

        public RSA()
            : this(defaultKeySize)
        {
        }

        public RSA(int keySize)
        {
            LegalKeySizesValue = new KeySizes[1];
            LegalKeySizesValue[0] = new KeySizes(384, 16384, 8);
            base.KeySize = keySize;
        }

        ~RSA()
        {
            // Zeroize private key
            Dispose(false);
        }

        private void GenerateKeyPair()
        {
            // p and q values should have a length of half the strength in bits
            int pbitlength = ((KeySize + 1) >> 1);
            int qbitlength = (KeySize - pbitlength);
            const uint uint_e = 17;
            e = uint_e; // fixed

            // generate p, prime and (p-1) relatively prime to e
            for (; ; )
            {
                p = BigInteger.GeneratePseudoPrime(pbitlength);
                if (p % uint_e != 1)
                    break;
            }
            // generate a modulus of the required length
            for (; ; )
            {
                // generate q, prime and (q-1) relatively prime to e,
                // and not equal to p
                for (; ; )
                {
                    q = BigInteger.GeneratePseudoPrime(qbitlength);
                    if ((q % uint_e != 1) && (p != q))
                        break;
                }

                // calculate the modulus
                n = p * q;
                if (n.BitCount() == KeySize)
                    break;

                // if we get here our primes aren't big enough, make the largest
                // of the two p and try again
                if (p < q)
                    p = q;
            }

            BigInteger pSub1 = (p - 1);
            BigInteger qSub1 = (q - 1);
            BigInteger phi = pSub1 * qSub1;

            // calculate the private exponent
            d = e.ModInverse(phi);

            // calculate the CRT factors
            dp = d % pSub1;
            dq = d % qSub1;
            qInv = q.ModInverse(p);

            keypairGenerated = true;
            isCRTpossible = true;

            if (KeyGenerated != null)
                KeyGenerated(this, null);
        }

        // overrides from RSA class

        public override int KeySize
        {
            get
            {
                // in case keypair hasn't been (yet) generated
                if (keypairGenerated)
                {
                    int ks = n.BitCount();
                    if ((ks & 7) != 0)
                        ks = ks + (8 - (ks & 7));
                    return ks;
                }
                else
                    return base.KeySize;
            }
        }
        public override string KeyExchangeAlgorithm
        {
            get { return "RSA-PKCS1-KeyEx"; }
        }

        // note: this property will exist in RSACryptoServiceProvider in
        // version 2.0 of the framework
        public bool PublicOnly
        {
            get { return ((d == null) || (n == null)); }
        }

        public override string SignatureAlgorithm
        {
            get { return "http://www.w3.org/2000/09/xmldsig#rsa-sha1"; }
        }

        public override byte[] DecryptValue(byte[] rgb)
        {
            if (m_disposed)
                throw new ObjectDisposedException("private key");

            // decrypt operation is used for signature
            if (!keypairGenerated)
                GenerateKeyPair();

            BigInteger input = new BigInteger(rgb);
            BigInteger r = null;

            // we use key blinding (by default) against timing attacks
            if (keyBlinding)
            {
                // x = (r^e * g) mod n 
                // *new* random number (so it's timing is also random)
                r = BigInteger.GenerateRandom(n.BitCount());
                input = r.ModPow(e, n) * input % n;
            }

            BigInteger output;
            // decrypt (which uses the private key) can be 
            // optimized by using CRT (Chinese Remainder Theorem)
            if (isCRTpossible)
            {
                // m1 = c^dp mod p
                BigInteger m1 = input.ModPow(dp, p);
                // m2 = c^dq mod q
                BigInteger m2 = input.ModPow(dq, q);
                BigInteger h;
                if (m2 > m1)
                {
                    // thanks to benm!
                    h = p - ((m2 - m1) * qInv % p);
                    output = m2 + q * h;
                }
                else
                {
                    // h = (m1 - m2) * qInv mod p
                    h = (m1 - m2) * qInv % p;
                    // m = m2 + q * h;
                    output = m2 + q * h;
                }
            }
            else
            {
                // m = c^d mod n
                output = input.ModPow(d, n);
            }

            if (keyBlinding)
            {
                // Complete blinding
                // x^e / r mod n
                output = output * r.ModInverse(n) % n;
                r.Clear();
            }

            byte[] result = output.GetBytes();
            // zeroize values
            input.Clear();
            output.Clear();
            return result;
        }

        public override byte[] EncryptValue(byte[] rgb)
        {
            if (m_disposed)
                throw new ObjectDisposedException("public key");

            if (!keypairGenerated)
                GenerateKeyPair();

            BigInteger input = new BigInteger(rgb);
            BigInteger output = input.ModPow(e, n);
            byte[] result = output.GetBytes();
            // zeroize value
            input.Clear();
            output.Clear();
            return result;
        }

        public override RSAParameters ExportParameters(bool includePrivateParameters)
        {
            if (m_disposed)
                throw new ObjectDisposedException("");

            if (!keypairGenerated)
                GenerateKeyPair();

            RSAParameters param = new RSAParameters();
            param.Exponent = e.GetBytes();
            param.Modulus = n.GetBytes();
            if (includePrivateParameters)
            {
                // some parameters are required for exporting the private key
                if (d == null)
                    throw new CryptographicException("Missing private key");
                param.D = d.GetBytes();
                // hack for bugzilla #57941 where D wasn't provided
                if (param.D.Length != param.Modulus.Length)
                {
                    byte[] normalizedD = new byte[param.Modulus.Length];
                    Buffer.BlockCopy(param.D, 0, normalizedD, (normalizedD.Length - param.D.Length), param.D.Length);
                    param.D = normalizedD;
                }
                // but CRT parameters are optionals
                if ((p != null) && (q != null) && (dp != null) && (dq != null) && (qInv != null))
                {
                    // and we include them only if we have them all
                    param.P = p.GetBytes();
                    param.Q = q.GetBytes();
                    param.DP = dp.GetBytes();
                    param.DQ = dq.GetBytes();
                    param.InverseQ = qInv.GetBytes();
                }
            }
            return param;
        }

        public override void ImportParameters(RSAParameters parameters)
        {
            if (m_disposed)
                throw new ObjectDisposedException("");

            // if missing "mandatory" parameters
            if (parameters.Exponent == null)
                throw new CryptographicException("Missing Exponent");
            if (parameters.Modulus == null)
                throw new CryptographicException("Missing Modulus");

            e = new BigInteger(parameters.Exponent);
            n = new BigInteger(parameters.Modulus);
            // only if the private key is present
            if (parameters.D != null)
                d = new BigInteger(parameters.D);
            if (parameters.DP != null)
                dp = new BigInteger(parameters.DP);
            if (parameters.DQ != null)
                dq = new BigInteger(parameters.DQ);
            if (parameters.InverseQ != null)
                qInv = new BigInteger(parameters.InverseQ);
            if (parameters.P != null)
                p = new BigInteger(parameters.P);
            if (parameters.Q != null)
                q = new BigInteger(parameters.Q);

            // we now have a keypair
            keypairGenerated = true;
            isCRTpossible = ((p != null) && (q != null) && (dp != null) && (dq != null) && (qInv != null));
        }

        protected override void Dispose(bool disposing)
        {
            if (!m_disposed)
            {
                // Always zeroize private key
                if (d != null)
                {
                    d.Clear();
                    d = null;
                }
                if (p != null)
                {
                    p.Clear();
                    p = null;
                }
                if (q != null)
                {
                    q.Clear();
                    q = null;
                }
                if (dp != null)
                {
                    dp.Clear();
                    dp = null;
                }
                if (dq != null)
                {
                    dq.Clear();
                    dq = null;
                }
                if (qInv != null)
                {
                    qInv.Clear();
                    qInv = null;
                }

                if (disposing)
                {
                    // clear public key
                    if (e != null)
                    {
                        e.Clear();
                        e = null;
                    }
                    if (n != null)
                    {
                        n.Clear();
                        n = null;
                    }
                }
            }
            // call base class 
            // no need as they all are abstract before us
            m_disposed = true;
        }

        public delegate void KeyGeneratedEventHandler(object sender, EventArgs e);

        public event KeyGeneratedEventHandler KeyGenerated;

        public override string ToXmlString(bool includePrivateParameters)
        {
            StringBuilder sb = new StringBuilder();
            RSAParameters rsaParams = ExportParameters(includePrivateParameters);
            try
            {
                sb.Append("<RSAKeyValue>");

                sb.Append("<Modulus>");
                sb.Append(Convert.ToBase64String(rsaParams.Modulus));
                sb.Append("</Modulus>");

                sb.Append("<Exponent>");
                sb.Append(Convert.ToBase64String(rsaParams.Exponent));
                sb.Append("</Exponent>");

                if (includePrivateParameters)
                {
                    if (rsaParams.P != null)
                    {
                        sb.Append("<P>");
                        sb.Append(Convert.ToBase64String(rsaParams.P));
                        sb.Append("</P>");
                    }
                    if (rsaParams.Q != null)
                    {
                        sb.Append("<Q>");
                        sb.Append(Convert.ToBase64String(rsaParams.Q));
                        sb.Append("</Q>");
                    }
                    if (rsaParams.DP != null)
                    {
                        sb.Append("<DP>");
                        sb.Append(Convert.ToBase64String(rsaParams.DP));
                        sb.Append("</DP>");
                    }
                    if (rsaParams.DQ != null)
                    {
                        sb.Append("<DQ>");
                        sb.Append(Convert.ToBase64String(rsaParams.DQ));
                        sb.Append("</DQ>");
                    }
                    if (rsaParams.InverseQ != null)
                    {
                        sb.Append("<InverseQ>");
                        sb.Append(Convert.ToBase64String(rsaParams.InverseQ));
                        sb.Append("</InverseQ>");
                    }
                    sb.Append("<D>");
                    sb.Append(Convert.ToBase64String(rsaParams.D));
                    sb.Append("</D>");
                }

                sb.Append("</RSAKeyValue>");
            }
            catch
            {
                if (rsaParams.P != null)
                    Array.Clear(rsaParams.P, 0, rsaParams.P.Length);
                if (rsaParams.Q != null)
                    Array.Clear(rsaParams.Q, 0, rsaParams.Q.Length);
                if (rsaParams.DP != null)
                    Array.Clear(rsaParams.DP, 0, rsaParams.DP.Length);
                if (rsaParams.DQ != null)
                    Array.Clear(rsaParams.DQ, 0, rsaParams.DQ.Length);
                if (rsaParams.InverseQ != null)
                    Array.Clear(rsaParams.InverseQ, 0, rsaParams.InverseQ.Length);
                if (rsaParams.D != null)
                    Array.Clear(rsaParams.D, 0, rsaParams.D.Length);
                throw;
            }

            return sb.ToString();
        }

        // internal for Mono 1.0.x in order to preserve public contract
        // they are public for Mono 1.1.x (for 1.2) as the API isn't froze ATM

#if NET_2_0
		public
#else
        internal
#endif
 bool UseKeyBlinding
        {
            get { return keyBlinding; }
            // you REALLY shoudn't touch this (true is fine ;-)
            set { keyBlinding = value; }
        }

#if NET_2_0
		public
#else
        internal
#endif
 bool IsCrtPossible
        {
            // either the key pair isn't generated (and will be 
            // generated with CRT parameters) or CRT is (or isn't)
            // possible (in case the key was imported)
            get { return (!keypairGenerated || isCRTpossible); }
        }
    }
}