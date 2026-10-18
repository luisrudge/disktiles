// DrawView: native drawing for window apps. C# builds a Drawing (rectangles, outlines,
// hatching, text) and sends it as one packed buffer with Bridge.Show(name, drawing). Any
// `DrawView { source: name }` in QML shows it, built straight into Qt Quick's scene graph:
// all shapes in one vertex-coloured geometry (one draw call), text as native text nodes.
// No JSON, no JavaScript, no full-window image. `view: [scale, x, y]` moves and zooms what
// is already built (a transform), so panning and zooming cost nothing until C# sends a
// fresh drawing.
#include <QFontMetricsF>
#include <QHash>
#include <QQuickItem>
#include <QQuickWindow>
#include <QSGGeometryNode>
#include <QSGTextNode>
#include <QSGTransformNode>
#include <QSGVertexColorMaterial>
#include <QTextLayout>
#include <QtMath>
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

// The buffer Drawing.cs writes (little-endian): "ODL1", double scale, x, y, int32 count, ops.
struct Reader {
    const uchar *p, *end;
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
            if (m_built && !m_built->triangles.empty()) {
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
