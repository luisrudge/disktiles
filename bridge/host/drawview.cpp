// DrawView: native drawing for window apps. C# builds a Drawing (rectangles, outlines,
// hatching, lines, areas, text) and sends it as one packed buffer with Bridge.Show(name,
// drawing). Any `DrawView { source: name }` in QML shows it, built straight into Qt Quick's
// scene graph: all shapes in one vertex-coloured geometry (one draw call; one image under Qt's
// software renderer), text as native text nodes.
// No JSON, no JavaScript, no full-window image. `view: [scale, x, y]` moves and zooms what
// is already built (a transform), so panning and zooming cost nothing until C# sends a
// fresh drawing.
#include <QFontMetricsF>
#include <QHash>
#include <QPainter>
#include <QQuickItem>
#include <QQuickWindow>
#include <QSGGeometryNode>
#include <QSGImageNode>
#include <QSGRendererInterface>
#include <QSGTextNode>
#include <QSGTransformNode>
#include <QSGVertexColorMaterial>
#include <QTextLayout>
#include <QtMath>
#include <algorithm>
#include <cstring>
#include <memory>
#include <vector>

namespace {

struct TextItem {
    QPointF topLeft;
    std::shared_ptr<QTextLayout> layout;
};

struct TextGroup {
    QColor color;
    std::vector<TextItem> items;
};

struct Scene {
    double scale = 1, originX = 0, originY = 0; // the view the drawing was made for
    std::vector<QSGGeometry::ColoredPoint2D> triangles;
    std::vector<TextGroup> texts;
};

QHash<QString, std::shared_ptr<const Scene>> scenes;

// Text layouts are the expensive part, and the same labels come back often: keep them.
struct FontKey {
    int pixelSize;
    bool bold;
    bool operator==(const FontKey &o) const { return pixelSize == o.pixelSize && bold == o.bold; }
};
size_t qHash(const FontKey &k, size_t seed = 0) { return ::qHash(k.pixelSize * 2 + (k.bold ? 1 : 0), seed); }

struct FontInfo {
    QFont font;
    QFontMetricsF metrics{QFont()};
};

const FontInfo &fontFor(int pixelSize, bool bold) {
    static QHash<FontKey, FontInfo> fonts;
    const FontKey key{pixelSize, bold};
    auto it = fonts.find(key);
    if (it == fonts.end()) {
        FontInfo info;
        info.font = QFont(QStringLiteral("monospace"));
        info.font.setPixelSize(pixelSize);
        info.font.setBold(bold);
        info.metrics = QFontMetricsF(info.font);
        it = fonts.insert(key, info);
    }
    return *it;
}

std::shared_ptr<QTextLayout> layoutFor(const QString &text, const FontInfo &f, int pixelSize, bool bold) {
    static QHash<QString, std::shared_ptr<QTextLayout>> cache;
    const QString key = QString::number(pixelSize) + (bold ? u'b' : u'n') + text;
    if (auto hit = cache.value(key)) return hit;
    if (cache.size() > 20000) cache.clear();
    auto layout = std::make_shared<QTextLayout>(text, f.font);
    layout->setCacheEnabled(true);
    layout->beginLayout();
    QTextLine line = layout->createLine();
    if (line.isValid()) line.setPosition(QPointF(0, 0));
    layout->endLayout();
    cache.insert(key, layout);
    return layout;
}

inline QSGGeometry::ColoredPoint2D vertex(float x, float y, quint32 argb) {
    // Premultiplied, as QSGVertexColorMaterial expects.
    const uchar a = argb >> 24;
    const auto pm = [a](uchar c) { return uchar(c * a / 255); };
    QSGGeometry::ColoredPoint2D v;
    v.set(x, y, pm(argb >> 16), pm(argb >> 8), pm(argb), a);
    return v;
}

void quad(std::vector<QSGGeometry::ColoredPoint2D> &t, QPointF a, QPointF b, QPointF c, QPointF d, quint32 argb) {
    t.push_back(vertex(a.x(), a.y(), argb));
    t.push_back(vertex(b.x(), b.y(), argb));
    t.push_back(vertex(c.x(), c.y(), argb));
    t.push_back(vertex(a.x(), a.y(), argb));
    t.push_back(vertex(c.x(), c.y(), argb));
    t.push_back(vertex(d.x(), d.y(), argb));
}

void rect(std::vector<QSGGeometry::ColoredPoint2D> &t, float x, float y, float w, float h, quint32 argb) {
    if (w <= 0 || h <= 0) return;
    quad(t, {x, y}, {x + w, y}, {x + w, y + h}, {x, y + h}, argb);
}

// Diagonal lines (bottom-left to top-right) every `spacing`, clipped to the rectangle.
void hatch(std::vector<QSGGeometry::ColoredPoint2D> &t, float x, float y, float w, float h, float spacing, float thickness, quint32 argb) {
    if (!(w > 0 && h > 0 && spacing > 0) || !std::isfinite(x + y + w + h + spacing)) return;
    const float lines = (w + h) / spacing;
    if (lines > 100000) return; // a runaway size, not a picture
    const float half = thickness / 2 / float(M_SQRT2);
    for (int i = 0; i < int(std::ceil(lines)); i++) {
        const float hx = x - h + i * spacing;
        const float c = hx + y + h; // points on the line: u + v = c
        const float u0 = std::max(x, c - (y + h)), u1 = std::min(x + w, c - y);
        if (u0 >= u1) continue;
        const QPointF p0(u0, c - u0), p1(u1, c - u1), n(half, half);
        quad(t, p0 - n, p1 - n, p1 + n, p0 + n, argb);
    }
}

void triangle(std::vector<QSGGeometry::ColoredPoint2D> &t, QPointF a, QPointF b, QPointF c, quint32 argb) {
    t.push_back(vertex(a.x(), a.y(), argb));
    t.push_back(vertex(b.x(), b.y(), argb));
    t.push_back(vertex(c.x(), c.y(), argb));
}

// Lines `width` thick through the points, one quad per segment. At a corner the two quads share
// the inner corner (where their inner edges meet), so nothing is drawn twice and a translucent
// line keeps one shade. The outer corner is mitred, or bevelled (a triangle) when the corner is
// sharper than 60°, where a mitre would stick out as a long spike. When a segment is too short
// for the inner corner (a sharp turn between close points), the quads end square at the point
// instead, and overlap a little inside the turn.
void polyline(std::vector<QSGGeometry::ColoredPoint2D> &t, const std::vector<QPointF> &p, float width, quint32 argb) {
    if (p.size() < 2 || !(width > 0) || !std::isfinite(width)) return;
    const qreal hw = width / 2;
    std::vector<QPointF> dir; // unit direction of each segment
    std::vector<qreal> len;
    dir.reserve(p.size() - 1);
    len.reserve(p.size() - 1);
    for (size_t i = 0; i + 1 < p.size(); i++) {
        const QPointF d = p[i + 1] - p[i];
        len.push_back(std::hypot(d.x(), d.y()));
        dir.push_back(len.back() > 0 ? d / len.back() : QPointF());
    }
    const auto normal = [](QPointF d) { return QPointF(-d.y(), d.x()); };
    QPointF left = p[0] + normal(dir[0]) * hw, right = p[0] - normal(dir[0]) * hw; // the current segment's start
    for (size_t i = 0; i < dir.size(); i++) {
        const QPointF n = normal(dir[i]), at = p[i + 1];
        QPointF endLeft = at + n * hw, endRight = at - n * hw, nextLeft = endLeft, nextRight = endRight;
        if (i + 1 < dir.size()) {
            const QPointF n2 = normal(dir[i + 1]), m = n + n2;
            nextLeft = at + n2 * hw;
            nextRight = at - n2 * hw;
            const qreal turn = dir[i].x() * dir[i + 1].y() - dir[i].y() * dir[i + 1].x();
            const qreal mlen = std::hypot(m.x(), m.y());
            const qreal cosHalf = mlen / 2; // cos of half the turn: corners are hw / cosHalf from the point
            if (turn != 0 && mlen > 1e-6) {
                // The inside of the turn is on the left when it turns left (turn > 0).
                const bool leftInside = turn > 0;
                QPointF &endIn = leftInside ? endLeft : endRight, &nextIn = leftInside ? nextLeft : nextRight;
                QPointF &endOut = leftInside ? endRight : endLeft, &nextOut = leftInside ? nextRight : nextLeft;
                const QPointF toInner = m / mlen * (hw / cosHalf) * (leftInside ? 1 : -1);
                // How far back along each segment the inner corner is.
                const qreal reach = hw * std::sqrt(std::max(0.0, 1 - cosHalf * cosHalf)) / cosHalf;
                const bool shared = reach <= std::min(len[i], len[i + 1]) / 2;
                if (shared) endIn = nextIn = at + toInner;
                if (cosHalf >= 0.5 && shared)
                    endOut = nextOut = at - toInner;
                else if (cosHalf >= 0.5) {
                    triangle(t, at, endOut, at - toInner, argb);
                    triangle(t, at, at - toInner, nextOut, argb);
                } else
                    triangle(t, shared ? endIn : at, endOut, nextOut, argb);
            }
        }
        quad(t, left, endLeft, endRight, right, argb);
        left = nextLeft;
        right = nextRight;
    }
}

// The area between the line through the points and the level `base`: a trapezoid per segment,
// split where the line crosses the base (a crossed quad would fill the wrong triangles).
void area(std::vector<QSGGeometry::ColoredPoint2D> &t, const std::vector<QPointF> &p, float base, quint32 argb) {
    if (!std::isfinite(base)) return;
    for (size_t i = 0; i + 1 < p.size(); i++) {
        const QPointF a = p[i], b = p[i + 1];
        const qreal da = a.y() - base, db = b.y() - base;
        if ((da < 0 && db > 0) || (da > 0 && db < 0)) {
            const QPointF cross(a.x() + (b.x() - a.x()) * da / (da - db), base);
            triangle(t, a, cross, {a.x(), base}, argb);
            triangle(t, cross, b, {b.x(), base}, argb);
        } else
            quad(t, a, b, {b.x(), base}, {a.x(), base}, argb);
    }
}

// The buffer Drawing.cs writes (little-endian): "ODL1", double scale, x, y, int32 count, ops.
struct Reader {
    const uchar *p, *end;
    // n (int32), then n float pairs: the points of a Polyline or Area. Points that aren't finite,
    // and repeats of the point before, are dropped. False when the buffer is too short.
    bool points(std::vector<QPointF> &out) {
        const qint32 n = get<qint32>();
        if (n < 0 || n > (end - p) / 8) return false;
        out.clear();
        for (int i = 0; i < n; i++) {
            const float x = get<float>(), y = get<float>();
            if (std::isfinite(x) && std::isfinite(y) && (out.empty() || out.back() != QPointF(x, y))) out.emplace_back(x, y);
        }
        return true;
    }
    template <class T>
    T get() {
        T v{};
        if (p + sizeof(T) <= end) {
            std::memcpy(&v, p, sizeof(T));
            p += sizeof(T);
        } else
            p = end;
        return v;
    }
};

std::shared_ptr<const Scene> parse(const uchar *data, int len) {
    auto scene = std::make_shared<Scene>();
    Reader r{data, data + len};
    if (len < 32 || std::memcmp(data, "ODL1", 4) != 0) return scene;
    r.p += 4;
    scene->scale = r.get<double>();
    scene->originX = r.get<double>();
    scene->originY = r.get<double>();
    const int count = r.get<qint32>();
    QHash<quint32, int> groupOf;
    std::vector<QPointF> points;
    for (int i = 0; i < count && r.p < r.end; i++) {
        const uchar op = r.get<uchar>();
        if (op == 1) { // Rect
            const float x = r.get<float>(), y = r.get<float>(), w = r.get<float>(), h = r.get<float>();
            rect(scene->triangles, x, y, w, h, r.get<quint32>());
        } else if (op == 2) { // Frame: an outline inside the rectangle
            const float x = r.get<float>(), y = r.get<float>(), w = r.get<float>(), h = r.get<float>(), s = r.get<float>();
            const quint32 c = r.get<quint32>();
            rect(scene->triangles, x, y, w, s, c);
            rect(scene->triangles, x, y + h - s, w, s, c);
            rect(scene->triangles, x, y + s, s, h - 2 * s, c);
            rect(scene->triangles, x + w - s, y + s, s, h - 2 * s, c);
        } else if (op == 3) { // Hatch
            const float x = r.get<float>(), y = r.get<float>(), w = r.get<float>(), h = r.get<float>();
            const float spacing = r.get<float>(), thickness = r.get<float>();
            hatch(scene->triangles, x, y, w, h, spacing, thickness, r.get<quint32>());
        } else if (op == 4) { // Text, vertically centred on y; cut to maxWidth when given
            const float x = r.get<float>(), y = r.get<float>(), size = r.get<float>(), maxWidth = r.get<float>();
            const quint32 c = r.get<quint32>();
            const bool bold = r.get<uchar>() & 1;
            const int n = r.get<qint32>();
            if (n < 0 || r.p + n > r.end) break;
            QString text = QString::fromUtf8(reinterpret_cast<const char *>(r.p), n);
            r.p += n;
            const int px = std::max(1, qRound(size));
            const FontInfo &f = fontFor(px, bold);
            if (maxWidth > 0 && f.metrics.horizontalAdvance(text) > maxWidth) {
                // Cut, don't elide: the caller decided what fits, like a clipped label.
                const qreal advance = f.metrics.horizontalAdvance(QLatin1Char('0'));
                int fit = std::min(int(text.size()), advance > 0 ? int(maxWidth / advance) + 1 : int(text.size()));
                while (fit > 0 && f.metrics.horizontalAdvance(text.left(fit)) > maxWidth) fit--;
                text.truncate(fit);
                if (text.isEmpty()) continue;
            }
            auto it = groupOf.find(c);
            if (it == groupOf.end()) {
                it = groupOf.insert(c, int(scene->texts.size()));
                scene->texts.push_back({QColor::fromRgba(c), {}});
            }
            const QPointF topLeft(x, y - (f.metrics.ascent() + f.metrics.descent()) / 2);
            scene->texts[*it].items.push_back({topLeft, layoutFor(text, f, px, bold)});
        } else if (op == 5) { // Polyline (Line is one with two points)
            const float width = r.get<float>();
            const quint32 c = r.get<quint32>();
            if (!r.points(points)) break;
            polyline(scene->triangles, points, width, c);
        } else if (op == 6) { // Area, down (or up) to a baseline
            const float base = r.get<float>();
            const quint32 c = r.get<quint32>();
            if (!r.points(points)) break;
            area(scene->triangles, points, base, c);
        } else
            break; // unknown op: stop rather than misread the rest
    }
    return scene;
}

class Hub : public QObject {
    Q_OBJECT
signals:
    void changed(const QString &name);
};

Hub *hub() {
    static Hub h;
    return &h;
}

class DrawView : public QQuickItem {
    Q_OBJECT
    Q_PROPERTY(QString source READ source WRITE setSource NOTIFY sourceChanged)
    Q_PROPERTY(QVariantList view READ view WRITE setView NOTIFY viewChanged)
public:
    DrawView() {
        setFlag(ItemHasContents);
        connect(hub(), &Hub::changed, this, [this](const QString &name) {
            if (name != m_source) return;
            m_scene = scenes.value(name);
            m_sceneDirty = true;
            update();
        });
    }
    QString source() const { return m_source; }
    void setSource(const QString &s) {
        if (s == m_source) return;
        m_source = s;
        m_scene = scenes.value(s);
        m_sceneDirty = true;
        update();
        emit sourceChanged();
    }
    QVariantList view() const { return m_view; }
    void setView(const QVariantList &v) {
        m_view = v;
        update();
        emit viewChanged();
    }

signals:
    void sourceChanged();
    void viewChanged();

protected:
    QSGNode *updatePaintNode(QSGNode *old, UpdatePaintNodeData *) override {
        auto *root = static_cast<QSGTransformNode *>(old);
        if (!root) {
            root = new QSGTransformNode;
            m_sceneDirty = true;
        }
        if (m_sceneDirty) {
            m_sceneDirty = false;
            while (QSGNode *child = root->firstChild()) {
                root->removeChildNode(child);
                delete child;
            }
            m_built = m_scene;
            // Qt Quick's software renderer (no GPU, QT_QUICK_BACKEND=software, check.sh --offscreen)
            // doesn't draw QSGVertexColorMaterial: there, paint the same triangles into an image.
            if (m_built && !m_built->triangles.empty() && window()->rendererInterface()->graphicsApi() == QSGRendererInterface::Software) {
                root->appendChildNode(softwareNode(*m_built));
            } else if (m_built && !m_built->triangles.empty()) {
                auto *geometry = new QSGGeometry(QSGGeometry::defaultAttributes_ColoredPoint2D(), int(m_built->triangles.size()));
                geometry->setDrawingMode(QSGGeometry::DrawTriangles);
                std::memcpy(geometry->vertexDataAsColoredPoint2D(), m_built->triangles.data(),
                            m_built->triangles.size() * sizeof(QSGGeometry::ColoredPoint2D));
                auto *node = new QSGGeometryNode;
                node->setGeometry(geometry);
                node->setMaterial(new QSGVertexColorMaterial);
                node->setFlags(QSGNode::OwnsGeometry | QSGNode::OwnsMaterial);
                root->appendChildNode(node);
            }
            if (m_built)
                for (const TextGroup &group : m_built->texts) {
                    QSGTextNode *text = window()->createTextNode();
                    text->setColor(group.color);
                    for (const TextItem &item : group.items) text->addTextLayout(item.topLeft, item.layout.get());
                    root->appendChildNode(text);
                }
        }
        // Map the drawing's view to the current one: screen = (layout - origin) * scale.
        QMatrix4x4 m;
        if (m_built && m_view.size() >= 3) {
            const double s = m_view[0].toDouble(), ox = m_view[1].toDouble(), oy = m_view[2].toDouble();
            const double k = s / m_built->scale;
            m.translate(float((m_built->originX - ox) * s), float((m_built->originY - oy) * s));
            m.scale(float(k), float(k));
        }
        root->setMatrix(m);
        return root;
    }

private:
    // The shapes as one image, for the software renderer (see updatePaintNode).
    QSGNode *softwareNode(const Scene &scene) {
        float x0 = scene.triangles[0].x, y0 = scene.triangles[0].y, x1 = x0, y1 = y0;
        for (const auto &v : scene.triangles) {
            x0 = std::min(x0, v.x);
            y0 = std::min(y0, v.y);
            x1 = std::max(x1, v.x);
            y1 = std::max(y1, v.y);
        }
        const QRectF area(std::floor(x0), std::floor(y0), std::ceil(x1) - std::floor(x0), std::ceil(y1) - std::floor(y0));
        // At most 4096 pixels each way (64 MB): shapes spread far apart are drawn at a lower
        // resolution rather than in a huge image.
        const qreal dpr = window()->effectiveDevicePixelRatio(), maxSide = 4096;
        const qreal sx = qMin(dpr, maxSide / qMax(1.0, area.width())), sy = qMin(dpr, maxSide / qMax(1.0, area.height()));
        QImage image(QSize(qBound(1, qCeil(area.width() * sx), int(maxSide)), qBound(1, qCeil(area.height() * sy), int(maxSide))), QImage::Format_ARGB32_Premultiplied);
        if (image.isNull()) return new QSGNode; // out of memory: no shapes rather than a crash
        image.fill(Qt::transparent);
        QPainter painter(&image);
        painter.scale(sx, sy);
        painter.translate(-area.topLeft());
        painter.setPen(Qt::NoPen);
        for (size_t i = 0; i + 2 < scene.triangles.size(); i += 3) {
            const auto &a = scene.triangles[i], &b = scene.triangles[i + 1], &c = scene.triangles[i + 2];
            // Every vertex of a shape has its colour, premultiplied (see vertex()).
            painter.setBrush(QColor::fromRgba(qUnpremultiply(qRgba(a.r, a.g, a.b, a.a))));
            const QPointF points[3] = {{a.x, a.y}, {b.x, b.y}, {c.x, c.y}};
            painter.drawPolygon(points, 3);
        }
        painter.end();
        QSGImageNode *node = window()->createImageNode();
        node->setTexture(window()->createTextureFromImage(image));
        node->setOwnsTexture(true);
        node->setRect(area);
        return node;
    }

    QString m_source;
    QVariantList m_view;
    std::shared_ptr<const Scene> m_scene, m_built;
    bool m_sceneDirty = true;
};

} // namespace

// Called on the UI thread by the bridge (Bridge.Show in C#).
void omasharpShowDrawing(const char *name, const unsigned char *data, int len) {
    const QString key = QString::fromUtf8(name);
    scenes.insert(key, parse(data, len));
    emit hub() -> changed(key);
}

void registerDrawView() {
    qmlRegisterType<DrawView>("Omasharp", 1, 0, "DrawView");
}

// Width of one monospace character per pixel of font size, for C# label layout.
double omasharpMonoAdvance() {
    QFont f(QStringLiteral("monospace"));
    f.setPixelSize(100);
    return QFontMetricsF(f).horizontalAdvance(QLatin1Char('0')) / 100.0;
}

#include "drawview.moc"
