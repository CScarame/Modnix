﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Reflection.BindingFlags;

namespace Sheepy.Modnix.Tests {

   [TestClass()]
   public class ModLoaderTest {

      [ClassInitializeAttribute] public static void TestInitialize ( TestContext _ ) => ModLoader.Setup();

      [TestCleanup] public void TestCleanup () {
         ModLoader.AllMods.Clear();
         ModLoader.EnabledMods.Clear();
      }

      private static void ResolveMods () => 
         typeof( ModLoader ).GetMethod( "ResolveMods", NonPublic | Static ).Invoke( null, new object[0] );

      [TestMethod()] public void DisabledModTest () {
         ModLoader.AllMods.Add( new ModEntry { Metadata = new ModMeta{ Id = "A" } } );
         ModLoader.AllMods.Add( new ModEntry { Disabled = true, Metadata = new ModMeta{ Id = "B" } } );
         ResolveMods();
         Assert.AreEqual( 2, ModLoader.AllMods.Count );
         Assert.AreEqual( 1, ModLoader.EnabledMods.Count );
      }

      private static Version Ver ( string val ) => Version.Parse( val );

      [TestMethod()] public void RequirementTest () {
         var ModnixMin = new ModEntry { Metadata = new ModMeta{ Id = "ModnixMin", Requires = new AppVer[]{ new AppVer{ Id = "Modnix", Min = Ver( "99.99" ) } } }.Normalise() };
         var ModnixOk  = new ModEntry { Metadata = new ModMeta{ Id = "ModnixOk" , Requires = new AppVer[]{ new AppVer{ Id = "Modnix", Min = Ver( "0.75" ) } } }.Normalise() };
         var ModnixMax = new ModEntry { Metadata = new ModMeta{ Id = "ModnixMax", Requires = new AppVer[]{ new AppVer{ Id = "Modnix", Max = Ver( "0.0" ) } } }.Normalise() };
         var PPMin = new ModEntry { Metadata = new ModMeta{ Id = "PPMin", Requires = new AppVer[]{ new AppVer{ Id = "PhoenixPoint", Min = Ver( "1.0.23456" ) } } }.Normalise() };
         var PPOk  = new ModEntry { Metadata = new ModMeta{ Id = "PPOk" , Requires = new AppVer[]{ new AppVer{ Id = "PhoenixPoint", Min = Ver( "1.0.12345" ) } } }.Normalise() };
         var PPMax = new ModEntry { Metadata = new ModMeta{ Id = "PPMax", Requires = new AppVer[]{ new AppVer{ Id = "Phoenix Point", Max = Ver( "1.0.4321" ) } } }.Normalise() };
         var PPMLMin = new ModEntry { Metadata = new ModMeta{ Id = "PPMLMin", Requires = new AppVer[]{ new AppVer{ Id = "ppml", Min = Ver( "99.99" ) } } }.Normalise() };
         var PPMLOk  = new ModEntry { Metadata = new ModMeta{ Id = "PPMLOk" , Requires = new AppVer[]{ new AppVer{ Id = "PhoenixPointModLoader", Min = Ver( "0.1" ) } } }.Normalise() };
         var PPMLMax = new ModEntry { Metadata = new ModMeta{ Id = "PPMLMax", Requires = new AppVer[]{ new AppVer{ Id = "Phoenix Point Mod Loader", Max = Ver( "0.0" ) } } }.Normalise() };
         var NonModnix = new ModEntry { Metadata = new ModMeta{ Id = "NonModnix", Requires = new AppVer[]{ new AppVer{ Id = "NonModnix" } } }.Normalise() };
         var Yes = new ModEntry { Metadata = new ModMeta{ Id = "NonModnix", Requires = new AppVer[]{ new AppVer{ Id = "ModnixOK" } } }.Normalise() };
         var No = new ModEntry { Metadata = new ModMeta{ Id = "NonModnix", Requires = new AppVer[]{ new AppVer{ Id = "ModnixOK" }, new AppVer{ Id = "ModnixMax" } } }.Normalise() };

         ModLoader.AllMods.Add( Yes );
         ModLoader.AllMods.Add( No );
         ModLoader.AllMods.Add( ModnixMin );
         ModLoader.AllMods.Add( ModnixOk );
         ModLoader.AllMods.Add( ModnixMax );
         ModLoader.AllMods.Add( PPMin );
         ModLoader.AllMods.Add( PPOk );
         ModLoader.AllMods.Add( PPMax );
         ModLoader.AllMods.Add( PPMLMin );
         ModLoader.AllMods.Add( PPMLOk );
         ModLoader.AllMods.Add( PPMLMax );
         ModLoader.AllMods.Add( NonModnix );

         ModLoader.GameVersion = new Version( "1.0.12345" );
         ResolveMods();

         Assert.AreEqual( 12, ModLoader.AllMods.Count );
         Assert.IsNotNull( ModnixMin.Notices, "ModnixMin" );
         Assert.IsFalse( ModnixOk.Disabled, "ModnixOk" );
         Assert.IsNotNull( ModnixMax.Notices, "ModnixMax" );
         Assert.IsNotNull( PPMin.Notices, "PPMin" );
         Assert.IsFalse( PPOk.Disabled, "PPOk" );
         Assert.IsNotNull( PPMax.Notices, "PPMax" );
         Assert.IsNotNull( PPMLMin.Notices, "PPMLMin" );
         Assert.IsFalse( PPMLOk.Disabled, "PPMLOk" );
         Assert.IsNotNull( PPMLMax.Notices, "PPMLMax" );
         Assert.IsNotNull( NonModnix.Notices, "NonModnix" );
         Assert.IsFalse( Yes.Disabled, "Yes" );
         Assert.IsNotNull( No.Notices, "No" );
         Assert.AreEqual( 4, ModLoader.EnabledMods.Count );
      }
   }
}
