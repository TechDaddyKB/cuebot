import CoreImage
import UIKit

/// Reads QR codes in a still image (a photo or screenshot of Cuebot's pairing code).
enum QRImageReader {
    /// The text of every QR code found, the pairing link first if there is one.
    static func codes(in data: Data) -> [String] {
        guard let image = CIImage(data: data, options: [.applyOrientationProperty: true]) else { return [] }
        let detector = CIDetector(ofType: CIDetectorTypeQRCode, context: nil, options: [CIDetectorAccuracy: CIDetectorAccuracyHigh])
        let found = (detector?.features(in: image) ?? []).compactMap { ($0 as? CIQRCodeFeature)?.messageString }
        return found.sorted { ($0.lowercased().hasPrefix("cuebot://") ? 0 : 1) < ($1.lowercased().hasPrefix("cuebot://") ? 0 : 1) }
    }
}
