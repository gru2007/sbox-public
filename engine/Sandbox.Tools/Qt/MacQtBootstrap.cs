using System;
using System.Runtime.InteropServices;

namespace Editor;

/// <summary>
/// The first time Qt processes events on macOS outside of QApplication::exec, QCocoaEventDispatcher calls
/// <c>[NSApp run]</c> once so Cocoa can finish initializing, and relies on a one-shot run loop entry observer
/// to stop it again. Our panel UI (launcher, splash) has already spun the main run loop by then, so that
/// observer has fired and nothing ever stops <c>[NSApp run]</c> - the editor sits on the splash screen forever.
/// Qt's own timers don't fire in there either, so we stop it ourselves from a CoreFoundation timer.
/// </summary>
internal static unsafe class MacQtBootstrap
{
	const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	const string ObjC = "/usr/lib/libobjc.A.dylib";

	const ulong NSEventTypeApplicationDefined = 15;

	static bool _done;
	static int _depth;
	static IntPtr _timer;

	/// <summary>
	/// Same as <c>g_pToolFramework2.Spin()</c>, but won't hang in Qt's NSApplication bootstrap on macOS.
	/// </summary>
	public static void Spin()
	{
		if ( _done || !OperatingSystem.IsMacOS() )
		{
			g_pToolFramework2.Spin();
			return;
		}

		Begin();

		try
		{
			g_pToolFramework2.Spin();
		}
		finally
		{
			End();
		}
	}

	static void Begin()
	{
		if ( _depth++ > 0 )
			return;

		// Qt only bootstraps when NSApp isn't already running
		if ( IsNSAppRunning() )
			return;

		_timer = CFRunLoopTimerCreate( IntPtr.Zero, CFAbsoluteTimeGetCurrent(), 0.01, 0, 0, &OnTimer, IntPtr.Zero );
		CFRunLoopAddTimer( CFRunLoopGetMain(), _timer, CommonModes );
	}

	static void End()
	{
		if ( --_depth > 0 )
			return;

		if ( _timer == IntPtr.Zero )
			return;

		// Qt has run its bootstrap now, it won't call [NSApp run] like this again
		_done = true;

		CFRunLoopTimerInvalidate( _timer );
		CFRelease( _timer );
		_timer = IntPtr.Zero;
	}

	[UnmanagedCallersOnly]
	static void OnTimer( IntPtr timer, IntPtr info )
	{
		if ( _depth == 0 || !IsNSAppRunning() )
			return;

		var app = NSApp;

		// stop only takes effect after the next event, so post an empty one to wake it up
		((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)MsgSend)( app, Sel( "stop:" ), IntPtr.Zero );

		var ev = ((delegate* unmanaged<IntPtr, IntPtr, ulong, CGPoint, ulong, double, long, IntPtr, short, long, long, IntPtr>)MsgSend)(
			ObjcGetClass( "NSEvent" ), Sel( "otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:" ),
			NSEventTypeApplicationDefined, default, 0, 0, 0, IntPtr.Zero, 0, 0, 0 );

		((delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, void>)MsgSend)( app, Sel( "postEvent:atStart:" ), ev, 1 );

		CFRunLoopTimerInvalidate( timer );
	}

	static bool IsNSAppRunning() => ((delegate* unmanaged<IntPtr, IntPtr, byte>)MsgSend)( NSApp, Sel( "isRunning" ) ) != 0;

	static IntPtr NSApp => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)MsgSend)( ObjcGetClass( "NSApplication" ), Sel( "sharedApplication" ) );

	static IntPtr _msgSend;
	static IntPtr MsgSend => _msgSend != IntPtr.Zero ? _msgSend : (_msgSend = NativeLibrary.GetExport( NativeLibrary.Load( ObjC ), "objc_msgSend" ));

	static IntPtr _commonModes;
	static IntPtr CommonModes => _commonModes != IntPtr.Zero ? _commonModes : (_commonModes = *(IntPtr*)NativeLibrary.GetExport( NativeLibrary.Load( CoreFoundation ), "kCFRunLoopCommonModes" ));

	[StructLayout( LayoutKind.Sequential )]
	struct CGPoint
	{
		public double X;
		public double Y;
	}

	[DllImport( ObjC, EntryPoint = "sel_registerName" )]
	static extern IntPtr Sel( [MarshalAs( UnmanagedType.LPUTF8Str )] string name );

	[DllImport( ObjC, EntryPoint = "objc_getClass" )]
	static extern IntPtr ObjcGetClass( [MarshalAs( UnmanagedType.LPUTF8Str )] string name );

	[DllImport( CoreFoundation )]
	static extern double CFAbsoluteTimeGetCurrent();

	[DllImport( CoreFoundation )]
	static extern IntPtr CFRunLoopGetMain();

	[DllImport( CoreFoundation )]
	static extern IntPtr CFRunLoopTimerCreate( IntPtr allocator, double fireDate, double interval, ulong flags, long order, delegate* unmanaged<IntPtr, IntPtr, void> callout, IntPtr context );

	[DllImport( CoreFoundation )]
	static extern void CFRunLoopAddTimer( IntPtr runLoop, IntPtr timer, IntPtr mode );

	[DllImport( CoreFoundation )]
	static extern void CFRunLoopTimerInvalidate( IntPtr timer );

	[DllImport( CoreFoundation )]
	static extern void CFRelease( IntPtr cf );
}
