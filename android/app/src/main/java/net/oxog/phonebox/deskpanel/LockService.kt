package net.oxog.phonebox.deskpanel

import android.accessibilityservice.AccessibilityService
import android.os.Build
import android.view.accessibility.AccessibilityEvent

/**
 * Turns the screen off and locks the phone, the same as a press of the power button (fingerprint and face unlock keep
 * working, unlike a device-admin lock). Android only lets an app do that through an accessibility service, which the
 * person switches on once in Settings › Accessibility. It asks for no events and cannot read the screen: its only job
 * is the lock action.
 */
class LockService : AccessibilityService() {
    override fun onServiceConnected() { instance = this }
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}
    override fun onDestroy() { if (instance === this) instance = null; super.onDestroy() }

    companion object {
        @Volatile private var instance: LockService? = null

        /** Android 9+ has the lock action. */
        val supported get() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.P

        /** False when the service is not switched on (or the phone is too old). */
        fun lock(): Boolean {
            if (!supported) return false
            val s = instance ?: return false
            return s.performGlobalAction(GLOBAL_ACTION_LOCK_SCREEN)
        }
    }
}
