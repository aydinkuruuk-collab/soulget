// Draws soulget's app icon (a record with a download arrow) at 1024 px: swift make-icon.swift out.png
import AppKit

let size: CGFloat = 1024
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1024, pixelsHigh: 1024, bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                           bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext
let space = CGColorSpaceCreateDeviceRGB()
func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 255) / 255, green: CGFloat((hex >> 8) & 255) / 255, blue: CGFloat(hex & 255) / 255, alpha: a)
}

// Rounded square with a soft shadow, like other Mac icons.
let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
let tilePath = CGPath(roundedRect: tile, cornerWidth: 186, cornerHeight: 186, transform: nil)
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: color(0x000000, 0.35))
ctx.addPath(tilePath); ctx.setFillColor(color(0x3B37B0)); ctx.fillPath()
ctx.restoreGState()
ctx.saveGState()
ctx.addPath(tilePath); ctx.clip()
let bg = CGGradient(colorsSpace: space, colors: [color(0x6E6AF2), color(0x2E2A96)] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(bg, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 100), options: [])

// The record.
let c = CGPoint(x: 512, y: 512)
func circle(_ r: CGFloat) -> CGRect { CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r) }
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: color(0x000000, 0.4))
ctx.setFillColor(color(0x15151B)); ctx.fillEllipse(in: circle(318))
ctx.restoreGState()
ctx.setLineWidth(3)
for r in stride(from: 150, through: 300, by: 18) {
    ctx.setStrokeColor(color(0xFFFFFF, r % 36 == 6 ? 0.07 : 0.04))
    ctx.strokeEllipse(in: circle(CGFloat(r)))
}
// Shine across the grooves.
ctx.saveGState()
ctx.addEllipse(in: circle(318)); ctx.clip()
let shine = CGGradient(colorsSpace: space, colors: [color(0xFFFFFF, 0), color(0xFFFFFF, 0.13), color(0xFFFFFF, 0)] as CFArray, locations: [0.3, 0.5, 0.7])!
ctx.drawLinearGradient(shine, start: CGPoint(x: 260, y: 780), end: CGPoint(x: 770, y: 250), options: [])
ctx.restoreGState()

// Centre label with a download arrow.
let label = CGGradient(colorsSpace: space, colors: [color(0xFFB340), color(0xFF6A3D)] as CFArray, locations: [0, 1])!
ctx.saveGState()
ctx.addEllipse(in: circle(122)); ctx.clip()
ctx.drawLinearGradient(label, start: CGPoint(x: 512, y: 634), end: CGPoint(x: 512, y: 390), options: [])
ctx.restoreGState()
ctx.setFillColor(color(0xFFFFFF))
ctx.addPath(CGPath(roundedRect: CGRect(x: 494, y: 480, width: 36, height: 92), cornerWidth: 10, cornerHeight: 10, transform: nil))
ctx.fillPath()
let head = CGMutablePath()
head.move(to: CGPoint(x: 444, y: 494)); head.addLine(to: CGPoint(x: 580, y: 494)); head.addLine(to: CGPoint(x: 512, y: 420)); head.closeSubpath()
ctx.addPath(head); ctx.fillPath()
ctx.restoreGState()
NSGraphicsContext.current = nil
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
