import Cocoa
import CoreGraphics
import ApplicationServices

// MARK: - Custom Flipped Canvas View (Zero Interpolation)
class CharacterCanvasView: NSView {
    override var isFlipped: Bool { return true }

    var onDraw: ((NSRect) -> Void)?

    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.current?.imageInterpolation = .none
        onDraw?(dirtyRect)
    }
}

// MARK: - Frameless Transparent Window
class CharacterWindow: NSPanel {
    init(contentRect: NSRect) {
        super.init(
            contentRect: contentRect,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        self.isOpaque = false
        self.backgroundColor = .clear
        self.hasShadow = false
        self.level = .floating
        self.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        self.isMovableByWindowBackground = true
        self.isReleasedWhenClosed = false
    }

    override var canBecomeKey: Bool { return false }
    override var canBecomeMain: Bool { return false }
}

// MARK: - App Delegate & Core Engine
class AppDelegate: NSObject, NSApplicationDelegate {
    private var window: CharacterWindow!
    private var canvasView: CharacterCanvasView!

    // 6 Layer Images
    private var imgBody: NSImage?
    private var imgLeftArmReady: NSImage?
    private var imgLeftArmType: NSImage?
    private var imgRightArmReady: NSImage?
    private var imgRightArmType: NSImage?
    private var imgMouseWithArm: NSImage?

    // Resolution detected dynamically
    private var baseWidth: CGFloat = 453.0
    private var baseHeight: CGFloat = 354.0

    // Settings
    private var currentScale: CGFloat = 1.0
    private var currentOpacity: CGFloat = 1.0
    private var isAlwaysOnTop: Bool = true

    // Mode
    enum OperationMode { case dualKeyboard, keyboardAndMouse }
    private var currentMode: OperationMode = .dualKeyboard

    // Typing State
    private var isLeftTyping: Bool = false
    private var isRightTyping: Bool = false
    private var activePressedKeys = Set<UInt16>()

    // Re-trigger Stroke Bounce (35ms lift on consecutive hits)
    private var leftBounceWorkItem: DispatchWorkItem?
    private var rightBounceWorkItem: DispatchWorkItem?
    private var reTriggerDuration: TimeInterval = 0.035

    // Mouse Tracking & Smart Auto-Switching
    private var maxRangeX: CGFloat = 10.0
    private var maxRangeY: CGFloat = 6.0
    private var smoothing: CGFloat = 0.20
    private var autoSwitchDelay: TimeInterval = 2.0 // 2.0s default

    private var lastRawMousePos: NSPoint = .zero
    private var targetOffsetX: CGFloat = 0.0
    private var targetOffsetY: CGFloat = 0.0
    private var currentOffsetX: CGFloat = 0.0
    private var currentOffsetY: CGFloat = 0.0
    private var isMousePressed: Bool = false
    private var lastMouseActivity: Date = Date()
    private var mouseTrackingTimer: Timer?

    // Global Event Tap
    private var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?

    // Left Hand macOS Virtual Keycodes
    private static let leftKeyCodes: Set<UInt16> = [
        53,                  // Esc
        122, 120, 99, 118, 96, 97, // F1 ~ F6
        50,                  // ` ~
        18, 19, 20, 21, 23,  // 1, 2, 3, 4, 5
        48,                  // Tab
        12, 13, 14, 15, 17,  // Q, W, E, R, T
        57,                  // CapsLock
        0, 1, 2, 3, 5,       // A, S, D, F, G
        56,                  // Left Shift
        6, 7, 8, 9, 11,      // Z, X, C, V, B
        59,                  // Left Control
        58,                  // Left Option
        55,                  // Left Command
        49                   // Space
    ]

    func applicationDidFinishLaunching(_ notification: Notification) {
        loadImages()
        setupWindow()
        setupContextMenu()
        setupMouseTrackingTimer()
        requestAccessibilityAndStartHook()
    }

    // MARK: - Resource Loading
    private func findResource(name: String) -> NSImage? {
        if let bundleUrl = Bundle.main.url(forResource: name, withExtension: "png") {
            return NSImage(contentsOf: bundleUrl)
        }
        let execDir = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent()
        let candidatePaths = [
            execDir.appendingPathComponent("resources/\(name).png").path,
            execDir.appendingPathComponent("\(name).png").path,
            "./resources/\(name).png",
            "./\(name).png"
        ]
        for path in candidatePaths {
            if FileManager.default.fileExists(atPath: path), let img = NSImage(contentsOfFile: path) {
                return img
            }
        }
        return nil
    }

    private func loadImages() {
        imgBody = findResource(name: "body")
        imgLeftArmReady = findResource(name: "leftArmReady")
        imgLeftArmType = findResource(name: "leftArmType")
        imgRightArmReady = findResource(name: "rightArmReady")
        imgRightArmType = findResource(name: "rightArmType")
        imgMouseWithArm = findResource(name: "mouseWithArm")

        if let body = imgBody {
            if let rep = body.representations.first {
                baseWidth = CGFloat(rep.pixelsWide > 0 ? rep.pixelsWide : Int(body.size.width))
                baseHeight = CGFloat(rep.pixelsHigh > 0 ? rep.pixelsHigh : Int(body.size.height))
            } else {
                baseWidth = body.size.width
                baseHeight = body.size.height
            }
            body.size = NSSize(width: baseWidth, height: baseHeight)
            imgLeftArmReady?.size = NSSize(width: baseWidth, height: baseHeight)
            imgLeftArmType?.size = NSSize(width: baseWidth, height: baseHeight)
            imgRightArmReady?.size = NSSize(width: baseWidth, height: baseHeight)
            imgRightArmType?.size = NSSize(width: baseWidth, height: baseHeight)
            imgMouseWithArm?.size = NSSize(width: baseWidth, height: baseHeight)
        }
    }

    // MARK: - Window Setup
    private func setupWindow() {
        let w = baseWidth * currentScale
        let h = baseHeight * currentScale
        let screenRect = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let initialRect = NSRect(
            x: screenRect.maxX - w - 40,
            y: screenRect.minY + 40,
            width: w,
            height: h
        )

        window = CharacterWindow(contentRect: initialRect)
        canvasView = CharacterCanvasView(frame: NSRect(x: 0, y: 0, width: w, height: h))
        canvasView.onDraw = { [weak self] dirtyRect in
            self?.drawCharacterLayers(dirtyRect)
        }

        window.contentView = canvasView
        window.alphaValue = currentOpacity
        window.makeKeyAndOrderFront(nil)
    }

    // MARK: - Multi-Layer Rendering (Exact Z-Index)
    private func drawCharacterLayers(_ rect: NSRect) {
        let w = canvasView.bounds.width
        let h = canvasView.bounds.height
        let boundsRect = NSRect(x: 0, y: 0, width: w, height: h)

        // 1. Mouse Arm Layer (BOTTOM / BEHIND BODY)
        if currentMode == .keyboardAndMouse {
            if let img = imgMouseWithArm {
                let clickY: CGFloat = isMousePressed ? (2.0 * currentScale) : 0
                let drawRect = NSRect(
                    x: currentOffsetX * currentScale,
                    y: currentOffsetY * currentScale + clickY,
                    width: w,
                    height: h
                )
                img.draw(in: drawRect)
            }
        }

        // 2. Base Body Layer (MIDDLE - covers upper mouse arm!)
        imgBody?.draw(in: boundsRect)

        // 3. Left Arm Layer (TOP - in front of desk/body)
        let leftImg = isLeftTyping ? imgLeftArmType : imgLeftArmReady
        leftImg?.draw(in: boundsRect)

        // 4. Right Arm Layer (TOP - only in DualKeyboard mode)
        if currentMode == .dualKeyboard {
            let rightImg = isRightTyping ? imgRightArmType : imgRightArmReady
            rightImg?.draw(in: boundsRect)
        }
    }

    // MARK: - Smart Auto-Switching & Mouse Tracking Loop
    private func setupMouseTrackingTimer() {
        lastRawMousePos = NSEvent.mouseLocation
        mouseTrackingTimer = Timer.scheduledTimer(withTimeInterval: 0.016, repeats: true) { [weak self] _ in
            self?.updateMouseTracking()
        }
    }

    private func updateMouseTracking() {
        let mousePos = NSEvent.mouseLocation
        let dx = mousePos.x - lastRawMousePos.x
        let dy = mousePos.y - lastRawMousePos.y

        let isMoving = (abs(dx) > 1 || abs(dy) > 1 || isMousePressed)

        if isMoving {
            lastRawMousePos = mousePos
            lastMouseActivity = Date()

            if currentMode != .keyboardAndMouse {
                currentMode = .keyboardAndMouse
            }

            guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
            let screenW = max(1, screen.frame.width)
            let screenH = max(1, screen.frame.height)

            // Normalized (-0.5 ~ +0.5). Cocoa Y is bottom-up, invert for flipped canvas.
            let normX = ((mousePos.x - screen.frame.minX) / screenW) - 0.5
            let normY = -(((mousePos.y - screen.frame.minY) / screenH) - 0.5)

            targetOffsetX = min(max(normX * maxRangeX * 2.0, -maxRangeX), maxRangeX)
            targetOffsetY = min(max(normY * maxRangeY * 2.0, -maxRangeY), maxRangeY)
        }

        // Smooth Lerp
        let diffX = targetOffsetX - currentOffsetX
        let diffY = targetOffsetY - currentOffsetY
        var offsetChanged = false

        if abs(diffX) > 0.05 || abs(diffY) > 0.05 {
            currentOffsetX += diffX * smoothing
            currentOffsetY += diffY * smoothing
            offsetChanged = true
        }

        // Return to DualKeyboard mode when mouse is inactive
        if currentMode == .keyboardAndMouse && !isMousePressed {
            if Date().timeIntervalSince(lastMouseActivity) >= autoSwitchDelay {
                currentMode = .dualKeyboard
                targetOffsetX = 0
                targetOffsetY = 0
                currentOffsetX = 0
                currentOffsetY = 0
                canvasView.needsDisplay = true
                return
            }
        }

        if currentMode == .keyboardAndMouse && offsetChanged {
            canvasView.needsDisplay = true
        }
    }

    // MARK: - Key Strokes & Re-trigger Bounce
    private func hasLeftKeyPressed() -> Bool {
        return activePressedKeys.contains { AppDelegate.leftKeyCodes.contains($0) }
    }

    private func hasRightKeyPressed() -> Bool {
        return activePressedKeys.contains { !AppDelegate.leftKeyCodes.contains($0) }
    }

    private func triggerLeftStroke() {
        if isLeftTyping {
            // Re-trigger lift (35ms) then slam down
            isLeftTyping = false
            canvasView.needsDisplay = true
            leftBounceWorkItem?.cancel()
            let item = DispatchWorkItem { [weak self] in
                if self?.hasLeftKeyPressed() == true {
                    self?.isLeftTyping = true
                    self?.canvasView.needsDisplay = true
                }
            }
            leftBounceWorkItem = item
            DispatchQueue.main.asyncAfter(deadline: .now() + reTriggerDuration, execute: item)
        } else {
            isLeftTyping = true
            leftBounceWorkItem?.cancel()
            canvasView.needsDisplay = true
        }
    }

    private func triggerRightStroke() {
        if isRightTyping {
            // Re-trigger lift (35ms) then slam down
            isRightTyping = false
            canvasView.needsDisplay = true
            rightBounceWorkItem?.cancel()
            let item = DispatchWorkItem { [weak self] in
                if self?.hasRightKeyPressed() == true {
                    self?.isRightTyping = true
                    self?.canvasView.needsDisplay = true
                }
            }
            rightBounceWorkItem = item
            DispatchQueue.main.asyncAfter(deadline: .now() + reTriggerDuration, execute: item)
        } else {
            isRightTyping = true
            rightBounceWorkItem?.cancel()
            canvasView.needsDisplay = true
        }
    }

    func onKeyDown(keyCode: UInt16) {
        let isRepeat = activePressedKeys.contains(keyCode)
        activePressedKeys.insert(keyCode)

        let isLeft = AppDelegate.leftKeyCodes.contains(keyCode)

        if currentMode == .keyboardAndMouse {
            if isLeft {
                if isRepeat { isLeftTyping = true }
                else { triggerLeftStroke() }
            } else {
                currentMode = .dualKeyboard
                if isRepeat { isRightTyping = true }
                else { triggerRightStroke() }
            }
        } else {
            if isLeft {
                if isRepeat { isLeftTyping = true }
                else { triggerLeftStroke() }
            } else {
                if isRepeat { isRightTyping = true }
                else { triggerRightStroke() }
            }
        }
    }

    func onKeyUp(keyCode: UInt16) {
        activePressedKeys.remove(keyCode)

        let hasLeft = hasLeftKeyPressed()
        let hasRight = hasRightKeyPressed()

        if !hasLeft {
            leftBounceWorkItem?.cancel()
            isLeftTyping = false
        }
        if !hasRight {
            rightBounceWorkItem?.cancel()
            isRightTyping = false
        }

        canvasView.needsDisplay = true
    }

    func onMouseDownGlobal(point: NSPoint) {
        if window.frame.contains(point) { return }

        isMousePressed = true
        lastMouseActivity = Date()

        if currentMode != .keyboardAndMouse {
            currentMode = .keyboardAndMouse
        }
        canvasView.needsDisplay = true
    }

    func onMouseUpGlobal() {
        if isMousePressed {
            isMousePressed = false
            canvasView.needsDisplay = true
        }
    }

    // MARK: - Context Menu
    private func setupContextMenu() {
        let menu = NSMenu(title: "CharacterType")

        // Scale Submenu
        let scaleMenu = NSMenu(title: "크기 배율 (Scale)")
        let scales: [(String, CGFloat)] = [
            ("50%", 0.5),
            ("75%", 0.75),
            ("100% (기본값)", 1.0),
            ("125%", 1.25),
            ("150%", 1.5),
            ("200%", 2.0),
            ("300%", 3.0)
        ]
        for (title, s) in scales {
            let item = NSMenuItem(title: title, action: #selector(scaleSelected(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = s
            item.state = (abs(s - currentScale) < 0.01) ? .on : .off
            scaleMenu.addItem(item)
        }
        let scaleItem = NSMenuItem(title: "크기 배율", action: nil, keyEquivalent: "")
        scaleItem.submenu = scaleMenu
        menu.addItem(scaleItem)

        // Opacity Submenu
        let opacityMenu = NSMenu(title: "투명도 (Opacity)")
        let opacities: [(String, CGFloat)] = [
            ("100% (불투명)", 1.0),
            ("80%", 0.8),
            ("60%", 0.6),
            ("40%", 0.4),
            ("20%", 0.2)
        ]
        for (title, op) in opacities {
            let item = NSMenuItem(title: title, action: #selector(opacitySelected(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = op
            item.state = (abs(op - currentOpacity) < 0.01) ? .on : .off
            opacityMenu.addItem(item)
        }
        let opacityItem = NSMenuItem(title: "투명도", action: nil, keyEquivalent: "")
        opacityItem.submenu = opacityMenu
        menu.addItem(opacityItem)

        // Mouse Range Preset Submenu
        let rangeMenu = NSMenu(title: "마우스 흔들림 반경")
        let ranges: [(String, CGFloat, CGFloat)] = [
            ("아주 얌전하게 (가로 6px / 세로 4px)", 6.0, 4.0),
            ("보통 (가로 10px / 세로 6px - 기본값)", 10.0, 6.0),
            ("자유롭게 (가로 16px / 세로 10px)", 16.0, 10.0)
        ]
        for (title, rx, ry) in ranges {
            let item = NSMenuItem(title: title, action: #selector(mouseRangeSelected(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = (rx, ry)
            item.state = (abs(rx - maxRangeX) < 0.1 && abs(ry - maxRangeY) < 0.1) ? .on : .off
            rangeMenu.addItem(item)
        }
        let rangeItem = NSMenuItem(title: "마우스 흔들림 반경", action: nil, keyEquivalent: "")
        rangeItem.submenu = rangeMenu
        menu.addItem(rangeItem)

        // Auto Switch Delay Submenu
        let delayMenu = NSMenu(title: "키보드 복귀 대기 시간")
        let delays: [(String, TimeInterval)] = [
            ("1.0초 (빠름)", 1.0),
            ("1.5초", 1.5),
            ("2.0초 (기본값)", 2.0),
            ("3.0초 (여유로움)", 3.0),
            ("4.0초", 4.0)
        ]
        for (title, d) in delays {
            let item = NSMenuItem(title: title, action: #selector(returnDelaySelected(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = d
            item.state = (abs(d - autoSwitchDelay) < 0.1) ? .on : .off
            delayMenu.addItem(item)
        }
        let delayItem = NSMenuItem(title: "키보드 복귀 대기 시간", action: nil, keyEquivalent: "")
        delayItem.submenu = delayMenu
        menu.addItem(delayItem)

        menu.addItem(NSMenuItem.separator())

        // Always on top
        let topItem = NSMenuItem(title: "항상 위에 유지", action: #selector(toggleAlwaysOnTop(_:)), keyEquivalent: "")
        topItem.target = self
        topItem.state = isAlwaysOnTop ? .on : .off
        menu.addItem(topItem)

        // Reset Position
        let resetItem = NSMenuItem(title: "위치 초기화", action: #selector(resetPosition), keyEquivalent: "")
        resetItem.target = self
        menu.addItem(resetItem)

        menu.addItem(NSMenuItem.separator())

        // Quit
        let quitItem = NSMenuItem(title: "종료 (Quit)", action: #selector(quitApp), keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(quitItem)

        canvasView.menu = menu
    }

    @objc private func scaleSelected(_ sender: NSMenuItem) {
        guard let s = sender.representedObject as? CGFloat else { return }
        currentScale = s
        if let submenu = sender.menu {
            for item in submenu.items { item.state = (item == sender) ? .on : .off }
        }
        let newW = baseWidth * currentScale
        let newH = baseHeight * currentScale
        let curFrame = window.frame
        let newFrame = NSRect(x: curFrame.minX, y: curFrame.maxY - newH, width: newW, height: newH)
        window.setFrame(newFrame, display: true, animate: false)
        canvasView.frame = NSRect(x: 0, y: 0, width: newW, height: newH)
        canvasView.needsDisplay = true
    }

    @objc private func opacitySelected(_ sender: NSMenuItem) {
        guard let op = sender.representedObject as? CGFloat else { return }
        currentOpacity = op
        if let submenu = sender.menu {
            for item in submenu.items { item.state = (item == sender) ? .on : .off }
        }
        window.alphaValue = currentOpacity
    }

    @objc private func mouseRangeSelected(_ sender: NSMenuItem) {
        guard let (rx, ry) = sender.representedObject as? (CGFloat, CGFloat) else { return }
        maxRangeX = rx
        maxRangeY = ry
        if let submenu = sender.menu {
            for item in submenu.items { item.state = (item == sender) ? .on : .off }
        }
    }

    @objc private func returnDelaySelected(_ sender: NSMenuItem) {
        guard let d = sender.representedObject as? TimeInterval else { return }
        autoSwitchDelay = d
        if let submenu = sender.menu {
            for item in submenu.items { item.state = (item == sender) ? .on : .off }
        }
    }

    @objc private func toggleAlwaysOnTop(_ sender: NSMenuItem) {
        isAlwaysOnTop.toggle()
        sender.state = isAlwaysOnTop ? .on : .off
        window.level = isAlwaysOnTop ? .floating : .normal
    }

    @objc private func resetPosition() {
        let w = baseWidth * currentScale
        let h = baseHeight * currentScale
        let screenRect = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let newFrame = NSRect(x: screenRect.maxX - w - 40, y: screenRect.minY + 40, width: w, height: h)
        window.setFrame(newFrame, display: true, animate: true)
    }

    @objc private func quitApp() {
        NSApplication.shared.terminate(nil)
    }

    // MARK: - Global Event Tap Hook
    func requestAccessibilityAndStartHook() {
        let checkOptPrompt = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        let options = [checkOptPrompt: true] as CFDictionary
        let isTrusted = AXIsProcessTrustedWithOptions(options)

        startEventTap()

        if !isTrusted {
            Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] timer in
                if AXIsProcessTrusted() {
                    timer.invalidate()
                    self?.restartEventTap()
                }
            }
        }
    }

    func startEventTap() {
        let mask = (1 << CGEventType.keyDown.rawValue) |
                   (1 << CGEventType.keyUp.rawValue) |
                   (1 << CGEventType.leftMouseDown.rawValue) |
                   (1 << CGEventType.leftMouseUp.rawValue) |
                   (1 << CGEventType.rightMouseDown.rawValue) |
                   (1 << CGEventType.rightMouseUp.rawValue)

        let refcon = UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())

        guard let tap = CGEvent.tapCreate(
            tap: .cghidEventTap,
            place: .headInsertEventTap,
            options: .listenOnly,
            eventsOfInterest: CGEventMask(mask),
            callback: eventTapCallback,
            userInfo: refcon
        ) else {
            return
        }

        eventTap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
    }

    func restartEventTap() {
        if let tap = eventTap {
            CGEvent.tapEnable(tap: tap, enable: false)
        }
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetCurrent(), source, .commonModes)
        }
        startEventTap()
    }
}

// MARK: - Event Tap Callback
func eventTapCallback(
    proxy: CGEventTapProxy,
    type: CGEventType,
    event: CGEvent,
    refcon: UnsafeMutableRawPointer?
) -> Unmanaged<CGEvent>? {
    guard let refcon = refcon else { return Unmanaged.passRetained(event) }
    let appDelegate = Unmanaged<AppDelegate>.fromOpaque(refcon).takeUnretainedValue()

    if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
        appDelegate.restartEventTap()
        return Unmanaged.passRetained(event)
    }

    let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))

    if type == .keyDown {
        DispatchQueue.main.async { appDelegate.onKeyDown(keyCode: keyCode) }
    } else if type == .keyUp {
        DispatchQueue.main.async { appDelegate.onKeyUp(keyCode: keyCode) }
    } else if type == .leftMouseDown || type == .rightMouseDown {
        let loc = NSEvent.mouseLocation
        DispatchQueue.main.async { appDelegate.onMouseDownGlobal(point: loc) }
    } else if type == .leftMouseUp || type == .rightMouseUp {
        DispatchQueue.main.async { appDelegate.onMouseUpGlobal() }
    }

    return Unmanaged.passRetained(event)
}

// MARK: - Entry Point
let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
