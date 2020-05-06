﻿using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CSharp;
using Sheepy.Logging;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Sheepy.Modnix.Actions {
   internal class EvalAction {

      private readonly ModEntry Mod;

      private Logger Log => Mod.CreateLogger();

      public EvalAction ( ModEntry mod ) {
         Mod = mod;
      }

      internal static void Run ( ModEntry mod, ModAction actions ) {
         if ( actions == null ) return;
         new EvalAction( mod ).Run( actions ).Wait();
      }
      
      private async Task Run ( ModAction action ) { try {
         PrepareCompiler();
         Log.Verbo( "Evaluating {0}", action.Eval );
         var result = await CSharpScript.EvaluateAsync( action.Eval, Options ).ConfigureAwait( false );
         Log.Info( result?.GetType().FullName ?? "null" );
         Log.Info( result );
      } catch ( Exception ex ) { Log.Error( ex ); } }

      private static readonly string[] Assemblies = new string[]{ "mscorlib.", "Assembly-CSharp.", "Cinemachine.", "0Harmony.", "Newtonsoft.Json." };
      private static ScriptOptions Options;

      private static void PrepareCompiler () { lock ( Assemblies ) {
         if ( Options != null ) return;

         Options = ScriptOptions.Default
            .WithLanguageVersion( Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp7_3 )
            .WithAllowUnsafe( false )
            .WithCheckOverflow( true )
            .WithFileEncoding( Encoding.UTF8 )
            .WithWarningLevel( 4 )
            .WithEmitDebugInformation( false )
            .WithOptimizationLevel( Microsoft.CodeAnalysis.OptimizationLevel.Release );

         var assemblies = new HashSet<Assembly>();
         var names = new HashSet<string>();
         var CodePath = Path.GetDirectoryName( ModLoader.LoaderPath );
         foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() ) try {
            Type[] types;
            if ( asm.IsDynamic || ! CodePath.Equals( Path.GetDirectoryName( asm.Location ) ) ) continue;
            var name = asm.FullName;
            ModLoader.Log.Trace( name );
            if ( name.StartsWith( "System" ) || name.StartsWith( "Unity." ) || name.StartsWith( "UnityEngine." ) ||
                  Assemblies.Any( e => name.StartsWith( e ) ) ) {
               assemblies.Add( asm );
               try {
                  types = asm.GetTypes();
               } catch ( ReflectionTypeLoadException rtlex ) { // Happens on System.dll
                  types = rtlex.Types;
               }
               foreach ( var t in types ) {
                  if ( t?.IsPublic != true ) continue;
                  if ( ! string.IsNullOrEmpty( t.Namespace ) )
                     names.Add( t.Namespace );
               }
            }
         } catch ( Exception ex ) { ModLoader.Log.Warn( ex ); }
         names.Remove( "System.Xml.Xsl.Runtime" );

         var usings = names.ToArray();
         Options = Options.WithReferences( assemblies.ToArray() ).WithImports( usings );
         string usingLog () => string.Join( ";", usings );
         ModLoader.Log.Verbo( (Func<string>) usingLog );
      } }
   }
}