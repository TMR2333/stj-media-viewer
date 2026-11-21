import MapKit
import STJCore
import SwiftUI

/// 地图视图：把带 GPS 坐标的素材钉在地图上，点选选中、双击进画廊。
struct PhotoMapView: View {
    @EnvironmentObject private var model: LibraryModel
    @State private var position: MapCameraPosition = .automatic

    private var located: [Asset] {
        model.assets.filter { $0.latitude != nil && $0.longitude != nil }
    }

    var body: some View {
        ZStack(alignment: .bottomLeading) {
            Map(position: $position) {
                ForEach(located) { asset in
                    if let coordinate = coordinate(of: asset) {
                        Annotation(asset.fileName, coordinate: coordinate, anchor: .bottom) {
                            MapPinView(asset: asset, isSelected: model.selection.contains(asset.id))
                                .onTapGesture {
                                    model.selection = [asset.id]
                                }
                                .onTapGesture(count: 2) {
                                    model.openGallery(asset)
                                }
                        }
                    }
                }
            }
            .mapStyle(.standard(elevation: .flat))
            .onAppear(perform: resetCamera)

            if located.isEmpty {
                Text("当前筛选结果没有带 GPS 坐标的素材")
                    .font(.system(size: 12))
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                    .stjGlassCapsule(surface: .regular)
                    .padding(16)
            } else {
                Text("地图上 \(located.count) 个素材 · 点选选中，双击进画廊")
                    .font(.system(size: 11))
                    .padding(.horizontal, 12)
                    .padding(.vertical, 7)
                    .stjGlassCapsule(surface: .regular)
                    .padding(16)
            }
        }
        .onChange(of: model.filter) { _, _ in resetCamera() }
    }

    private func coordinate(of asset: Asset) -> CLLocationCoordinate2D? {
        guard let latitude = asset.latitude, let longitude = asset.longitude,
              (-90...90).contains(latitude), (-180...180).contains(longitude) else { return nil }
        return CLLocationCoordinate2D(latitude: latitude, longitude: longitude)
    }

    private func resetCamera() {
        let coordinates = located.compactMap { coordinate(of: $0) }
        guard let first = coordinates.first else {
            position = .region(MKCoordinateRegion(center: CLLocationCoordinate2D(latitude: 31.23, longitude: 121.47),
                                                  span: MKCoordinateSpan(latitudeDelta: 12, longitudeDelta: 12)))
            return
        }
        var minLat = first.latitude, maxLat = first.latitude
        var minLon = first.longitude, maxLon = first.longitude
        for coordinate in coordinates {
            minLat = min(minLat, coordinate.latitude)
            maxLat = max(maxLat, coordinate.latitude)
            minLon = min(minLon, coordinate.longitude)
            maxLon = max(maxLon, coordinate.longitude)
        }
        let center = CLLocationCoordinate2D(latitude: (minLat + maxLat) / 2, longitude: (minLon + maxLon) / 2)
        let span = MKCoordinateSpan(latitudeDelta: max((maxLat - minLat) * 1.6, 0.02),
                                    longitudeDelta: max((maxLon - minLon) * 1.6, 0.02))
        position = .region(MKCoordinateRegion(center: center, span: span))
    }
}

private struct MapPinView: View {
    var asset: Asset
    var isSelected: Bool

    var body: some View {
        VStack(spacing: 2) {
            ThumbnailView(asset: asset, targetSize: 120)
                .frame(width: 44, height: 44)
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .strokeBorder(isSelected ? Color.accentColor : Color.white.opacity(0.9),
                                      lineWidth: isSelected ? 3 : 1.5)
                )
                .shadow(radius: 2, y: 1)
            Image(systemName: asset.isVideo ? "video.fill" : "photo.fill")
                .font(.system(size: 8, weight: .bold))
                .foregroundStyle(.white)
                .padding(3)
                .background(Circle().fill(Color.accentColor.opacity(0.85)))
        }
        .help(asset.fileName)
    }
}
