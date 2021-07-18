﻿namespace Mega.Tests.Algorithm
{
	using System.Numerics;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public sealed class RsaKeyTests
	{
		[TestMethod]
		public void RsaPrivateKeyImport_FromMpiArray_SeemsToWork()
		{
			var passwordKey = Algorithms.DeriveAesKey("creation");
			var encryptedMasterKey = Algorithms.Base64Decode("aXaYqXimDxtnOx2OunCXgg");

			var masterKey = Algorithms.AesDecryptKey(encryptedMasterKey, passwordKey);

			var encryptedRsaKeyMpiArray = Algorithms.Base64Decode("3Y1jUdtxUHgx9tK2eslnoSIORipVFHMainkCIWH6fRFFcb6Z8KC_ipK9Jeebs5u7saBbDDUR6atag2BBP3LIzLEvEHAVmjgAZaKbX0dfZKVI4wkGAmM4-8s3z9ke_EIveslG41TvPygAK-seHYfkptkp-1eZdVrZmdqlumagqcZDksB24vr0CrD7hYD9HSKOPTjFSEFXyoK10N9iB1DBqNhw-2cVLqysUf4FTMyG1Ewe7fMvKkJdes2lU2mQue_5qxj890M2Lhq-5SS_rUqaWLkCOnWTaLqsCARB09yshyqCwA2eNecNhO12nB28D5hTIabjYUmvQAZvodrTSKCozvBTcDBiMFukBITtwl-lpRBUD5GUQ-T2oP-ozyEySWUzFh0_h2xKUVvwmAoH44VZ63dwUETeP0TMfoJigJcmLKaSwE1m9ur3x0iVC3asYo-qGG9f8Xqm8_aclcGzGQ8gid2vy5kpQrAk50L1m1PnvWV3t_Bqq8ovWfGybWVFaoPykHkPf2fbJACeAHtq1kKbvCh1P8LPEWpdcM6I6wisw_UD1EwxBuzMSo0JPqEHMsYKoRy42muRv8X81_OYRkVVCEfQaoRneBBCQismhhduCRRw_aErVZl2fxBQJ66yUDCpYz2Pix_Qpk9CuN_tIW0GoTIlDXM-Xg3Ew9BGrhkrWz283ZRMGZ9xiPNCOjNkWGZJ25d-HirmLfWj_z2kg12Ix7uK7ZOsOU5v0zTcnc1YfriXO7ljZHchoToU_XK1I-Lqi2H2nRqwxKk1wEePz_nPDveU_0oy-HWxoGOnUptNyb0rpYb_zZ0wfyfcXR_a_b9omOBaFEdoz5G5B2jdXqjYXKYYd4MV3kFgQAQBkQIficc");

			// This is the MPI array containing the RSA private key parameters.
			var rsaKeyMpiArray = Algorithms.AesDecryptKey(encryptedRsaKeyMpiArray, masterKey);

			var rsaPrivateKey = Algorithms.MpiArrayBytesToRsaPrivateKey(rsaKeyMpiArray);

			Assert.AreEqual(BigInteger.Parse("148896287039501678969147386479458178246000691707699594019852371996225136011987881033904404601666619814302065310828663028471342954821076961960815187788626496609581811628527023262215778397482476920164511192915070597893567835708908996890192512834283979142025668876250608381744928577381330716218105191496818716653"), rsaPrivateKey.P);

			Assert.AreEqual(BigInteger.Parse("119975764355551220778509708561576785383941026741388506773912560292606151764383332427604710071170171329268379604135341015979284377183953677973647259809025842247294479469402755370769383988530082830904396657573472653613365794770434467132057189606171325505138499276437937752474437953713231209677228298628994462467"), rsaPrivateKey.Q);

			Assert.AreEqual(BigInteger.Parse("1050820343956927572934612076472252685571974129712443554815513287152988684406403906076515668704928544975690979813088696470039543056941876104251006278039055944306922545547445651931155520381669260360756401313575997705078427390694344434682513918993867897491831570971092873803705042510495766113744352563287063082924679334868600466200729287883561166127597013570469069087493218333754090415201625690412387092800057802674340482431338713902173315497845649798915108362617461018268539059230386716408633534832438888093711421967131642544319111454229844308951507451138039552416804729190939602115000097721815735491982407386629599049"), rsaPrivateKey.D);

			Assert.AreEqual(BigInteger.Parse("17863945847267768739888405300028295654723560205111540431863725881600807634908866403300766367983785264586746656822507839990672231968011893772267106726663951053217683274306576082829643846488377426132858822330791960986333265641803855389602736622895754257361136706508578854662985722678428023933653993575880072409988420744161260825160054989061574787799090949147062275281149643962650824834798850198519695250437773789034233116296762180787573695468406686516019289762149176167441455104846352811931932478164020848662002023929781174760358525201250817274425329109801977038249848548934519370174368192365429429259034415698516362951"), rsaPrivateKey.N);
		}

		[TestMethod]
		public void RsaPublicKeyImport_FromMpiArray_SeemsToWork()
		{
			var rsaKeyMpiArray = Algorithms.Base64Decode("CACNgnxQOAmYi6N11CGOetcECbb4Mi3jM12iNvIZb1szdYfb2A_AW6Ds-vHy2NiYcBwGVMJ0rKSnSfA4KPLxFGwkNrrequBy9brLYw6CP0d0OanNJPdmpDMOdSW8IhWPO4R4eThhTkWQKy_jcCBsaKt4DcPuVBZi1q5lgUpVi9GxeWZjK1HH72I6UPDbzpEluRl55zrKuVR4DdTwBv0weAUM8FSk3iJWQ0bXccPQjHnDJ4nFBXBaCx-yZh146P3sYyPJ5c6QE2rX2MMxbJtUeZEc-ywWHt8xHmoA19Kfg5T9z-O8eFplrIRY30t6PHdkf61HNUg-BS1nbWIW_js-D8bHAAUR");

			var publicKey = Algorithms.MpiArrayBytesToRsaPublicKey(rsaKeyMpiArray);

			Assert.AreEqual(Algorithms.RsaE, publicKey.E);

			Assert.AreEqual(BigInteger.Parse("17863945847267768739888405300028295654723560205111540431863725881600807634908866403300766367983785264586746656822507839990672231968011893772267106726663951053217683274306576082829643846488377426132858822330791960986333265641803855389602736622895754257361136706508578854662985722678428023933653993575880072409988420744161260825160054989061574787799090949147062275281149643962650824834798850198519695250437773789034233116296762180787573695468406686516019289762149176167441455104846352811931932478164020848662002023929781174760358525201250817274425329109801977038249848548934519370174368192365429429259034415698516362951"), publicKey.N);
		}
	}
}