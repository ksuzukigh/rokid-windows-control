package io.github.ksuzukigh.rokidcontrol.device;

import android.app.UiAutomation;
import android.accessibilityservice.AccessibilityServiceInfo;
import android.graphics.Rect;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.SystemClock;
import android.view.KeyEvent;
import android.view.accessibility.AccessibilityNodeInfo;
import android.view.accessibility.AccessibilityWindowInfo;
import java.util.ArrayDeque;
import java.util.List;

/** Shell-only RV101 navigation read. Never disable the user's accessibility services. */
public final class RokidUiReader {
    public static void main(String[] args) {
        int status = 0;
        try { run(args); }
        catch (Exception failure) { System.err.println("RokidUiReader: " + failure.getMessage()); status = 1; }
        System.exit(status);
    }

    private static void run(String[] args) throws Exception {
        HandlerThread thread = new HandlerThread("RokidControlUiReader");
        thread.start();
        UiAutomation ui = null;
        try {
            Class<?> connectionType = Class.forName("android.app.IUiAutomationConnection");
            Object connection = Class.forName("android.app.UiAutomationConnection").getConstructor().newInstance();
            ui = UiAutomation.class.getConstructor(Looper.class, connectionType)
                .newInstance(thread.getLooper(), connection);
            UiAutomation.class.getMethod("connect", int.class).invoke(ui,
                UiAutomation.FLAG_DONT_SUPPRESS_ACCESSIBILITY_SERVICES);
            AccessibilityServiceInfo info = ui.getServiceInfo();
            info.flags |= AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS
                | AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
                | AccessibilityServiceInfo.FLAG_INCLUDE_NOT_IMPORTANT_VIEWS;
            ui.setServiceInfo(info);
            if (args.length == 1 && ("focus-apps".equals(args[0]) || "apps".equals(args[0]))) {
                try (AppSelection selection = AppSelection.awaitVisible(ui)) {
                    boolean focused = selection.center.isFocused();
                    if ("focus-apps".equals(args[0])) {
                        // A touch on the page indicator leaves Android in touch mode. Its next
                        // DPAD event otherwise restores a stale focus instead of moving one item.
                        // Focus the item actually displayed in the center without activating it.
                        long deadline = SystemClock.uptimeMillis() + 4500;
                        int stableReads = 0;
                        while (stableReads < 2 && SystemClock.uptimeMillis() < deadline) {
                            try (AppSelection current = AppSelection.read(ui)) {
                                if (!current.center.isFocused()) {
                                    stableReads = 0;
                                    if (!current.center.performAction(AccessibilityNodeInfo.ACTION_FOCUS))
                                        throw new IllegalStateException("Could not focus the visible launcher app");
                                } else if (current.index != selection.index) {
                                    // YodaOS can asynchronously replace the requested focus with
                                    // its old default and recycle the original view. Restore the
                                    // captured adapter position using fresh, focused nodes only.
                                    stableReads = 0;
                                    int key = current.index < selection.index ? KeyEvent.KEYCODE_DPAD_RIGHT : KeyEvent.KEYCODE_DPAD_LEFT;
                                    long now = SystemClock.uptimeMillis();
                                    if (!ui.injectInputEvent(new KeyEvent(now, now, KeyEvent.ACTION_DOWN, key, 0), true)
                                        || !ui.injectInputEvent(new KeyEvent(now, now, KeyEvent.ACTION_UP, key, 0), true))
                                        throw new IllegalStateException("Could not restore the visible launcher selection");
                                } else {
                                    if (!current.label.equals(selection.label))
                                        throw new IllegalStateException("Launcher items changed while focusing");
                                    stableReads++;
                                }
                            }
                            if (stableReads < 2) Thread.sleep(160);
                        }
                        if (stableReads < 2) throw new IllegalStateException("Launcher app focus did not settle");
                        focused = true;
                    }
                    System.out.println("<apps label=\"" + escape(selection.label) + "\" focused=\""
                        + focused + "\" index=\"" + selection.index + "\"/>");
                }
                return;
            }
            if (args.length != 0) throw new IllegalArgumentException("Unknown reader mode");
            String match = null;
            int matches = 0;
            for (AccessibilityWindowInfo window : ui.getWindows()) {
                if (window.getType() != AccessibilityWindowInfo.TYPE_APPLICATION) continue;
                AccessibilityNodeInfo root = window.getRoot();
                if (root == null) continue;
                if (!"com.rokid.os.sprite.launcher".contentEquals(root.getPackageName() == null ? "" : root.getPackageName())) {
                    root.recycle(); continue;
                }
                ArrayDeque<AccessibilityNodeInfo> nodes = new ArrayDeque<>(); nodes.add(root);
                int visited = 0;
                while (!nodes.isEmpty()) {
                    AccessibilityNodeInfo node = nodes.removeFirst();
                    if (visited++ >= 500) { node.recycle(); continue; }
                    if ("com.rokid.os.sprite.launcher:id/indicator".equals(node.getViewIdResourceName())
                        && node.isVisibleToUser() && node.isEnabled() && node.isClickable()) {
                        Rect r = new Rect(); node.getBoundsInScreen(r);
                        if (r.left >= 0 && r.top >= 0 && r.right <= 480 && r.bottom <= 640
                            && r.width() > 0 && r.height() > 0) {
                            matches++;
                            match = "<hierarchy><node package=\"com.rokid.os.sprite.launcher\""
                                + " resource-id=\"com.rokid.os.sprite.launcher:id/indicator\""
                                + " enabled=\"true\" clickable=\"true\" bounds=\"[" + r.left + "," + r.top
                                + "][" + r.right + "," + r.bottom + "]\"/></hierarchy>";
                        }
                    }
                    for (int i = 0; i < node.getChildCount(); i++) {
                        AccessibilityNodeInfo child = node.getChild(i);
                        if (child != null) nodes.addLast(child);
                    }
                    node.recycle();
                }
            }
            if (matches != 1) throw new IllegalStateException("No unique RV101 navigation indicator");
            System.out.println(match);
        } finally {
            if (ui != null) UiAutomation.class.getMethod("disconnect").invoke(ui);
            thread.quitSafely();
        }
    }

    private static String escape(String value) {
        return value.replace("&", "&amp;").replace("\"", "&quot;")
            .replace("<", "&lt;").replace(">", "&gt;");
    }

    private static final class AppSelection implements AutoCloseable {
        final AccessibilityNodeInfo center;
        final String label;
        final int index;

        AppSelection(AccessibilityNodeInfo center, String label, int index) { this.center = center; this.label = label; this.index = index; }

        static AppSelection awaitVisible(UiAutomation ui) throws InterruptedException {
            // The indicator tap returns before ViewPager has exposed its new page.
            // Wait for that page's nodes; never guess a target from the old page.
            for (int attempt = 0; ; attempt++) {
                try { return read(ui); }
                catch (IllegalStateException pending) {
                    if (attempt >= 12) throw pending;
                    Thread.sleep(100);
                }
            }
        }

        static AppSelection read(UiAutomation ui) {
            AccessibilityNodeInfo root = ui.getRootInActiveWindow();
            if (root == null) throw new IllegalStateException("No active launcher window");
            AccessibilityNodeInfo center = null;
            List<AccessibilityNodeInfo> carousels = null;
            List<AccessibilityNodeInfo> labels = null;
            try {
                if (!"com.rokid.os.sprite.launcher".contentEquals(root.getPackageName() == null ? "" : root.getPackageName()))
                    throw new IllegalStateException("The launcher is not active");
                carousels = root.findAccessibilityNodeInfosByViewId("com.rokid.os.sprite.launcher:id/app_recycler");
                labels = root.findAccessibilityNodeInfosByViewId("com.rokid.os.sprite.launcher:id/app_center_name_tv");
                Rect screen = new Rect(); root.getBoundsInScreen(screen);
                int visibleCarousels = 0;
                int centers = 0;
                for (AccessibilityNodeInfo carousel : carousels) {
                    if (!carousel.isVisibleToUser() || !carousel.isEnabled()) continue;
                    visibleCarousels++;
                    Rect bounds = new Rect(); carousel.getBoundsInScreen(bounds);
                    if (bounds.isEmpty() || !screen.contains(bounds)) continue;
                    for (int i = 0; i < carousel.getChildCount(); i++) {
                        AccessibilityNodeInfo child = carousel.getChild(i);
                        if (child == null) continue;
                        Rect r = new Rect(); child.getBoundsInScreen(r);
                        if (child.isVisibleToUser() && child.isEnabled() && child.isFocusable() && child.isClickable()
                            && !r.isEmpty() && screen.contains(r) && r.contains(bounds.centerX(), bounds.centerY())) {
                            centers++;
                            if (center != null) center.recycle();
                            center = child;
                        } else child.recycle();
                    }
                }
                String label = null;
                int visibleLabels = 0;
                for (AccessibilityNodeInfo node : labels) {
                    if (node.isVisibleToUser() && node.getText() != null && node.getText().length() > 0) {
                        visibleLabels++;
                        label = node.getText().toString();
                    }
                }
                if (visibleCarousels != 1 || centers != 1 || visibleLabels != 1)
                    throw new IllegalStateException("No unique visible launcher app");
                AccessibilityNodeInfo.CollectionItemInfo item = center.getCollectionItemInfo();
                if (item == null || item.getRowIndex() != 0 || item.getColumnIndex() < 0)
                    throw new IllegalStateException("No launcher adapter position");
                AppSelection result = new AppSelection(center, label, item.getColumnIndex());
                center = null;
                return result;
            } finally {
                if (center != null) center.recycle();
                if (carousels != null) for (AccessibilityNodeInfo node : carousels) node.recycle();
                if (labels != null) for (AccessibilityNodeInfo node : labels) node.recycle();
                root.recycle();
            }
        }

        public void close() { center.recycle(); }
    }
}
