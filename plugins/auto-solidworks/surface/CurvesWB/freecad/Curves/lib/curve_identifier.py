import FreeCAD
import Part
from math import pi
from freecad.Curves.lib.geometry import lines_intersection
from freecad.Curves.lib.logger import FCLogger


class CurveIdentifier:
    "Tries to identify canonical curve of an edge"

    def __init__(self, edge, num_samples=10, tol=1e-7):
        self.edge = edge
        self.num_samples = num_samples
        self.fp, self.lp = self.edge.ParameterRange
        self.tol = tol
        self.gap = None
        self.max_dev_param = None
        self.logger = FCLogger("Debug", "CurveIdentifier")

    def is_canonical(self):
        types = ('Part::GeomCircle',
                 'Part::GeomEllipse',
                 'Part::GeomLine',
                 'Part::GeomParabola'
                 'Part::GeomHyperbola')
        if self.edge.Curve.TypeId in types:
            return True
        return False

    def uniform_U(self):
        "Uniform generator of u parameters in the source edge bounds"
        urange = self.lp - self.fp
        for i in range(self.num_samples):
            u = self.fp + i * urange / (self.num_samples - 1)
            yield u

    def sample_lines(self):
        "Return a list of normal lines along source edge"
        lines = []
        for u in self.uniform_U():
            pt = self.edge.valueAt(u)
            try:
                n = self.edge.normalAt(u)
            except Part.OCCError:
                return
            lines.append(Part.Line(pt, pt + n))
        return lines

    def get_line(self):
        pt1 = self.edge.valueAt(self.fp)
        pt2 = self.edge.valueAt(self.lp)
        # chord = Part.makeLine(pt1, pt2)
        chord_length = pt1.distanceToPoint(pt2)
        if chord_length < self.tol:
            self.logger.info("Curve is closed or degenerated")
            return False
        cyl = Part.Cylinder()
        cyl.Axis = pt2 - pt1
        cyl.Radius = self.edge.Length / 10
        cyl.Center = pt1
        u0, u1 = cyl.bounds()[:2]
        rts = Part.RectangularTrimmedSurface(cyl, u0, u1, 0.0, chord_length)
        cyl_face = rts.toShape()
        d, pts, info = self.edge.distToShape(cyl_face)
        self.gap = cyl.Radius - d
        self.max_dev_param = info[0][2]
        if self.gap < self.tol:
            return Part.makeLine(pt1, pt2)
        return False

    def get_circle(self):
        center = False
        lines = self.sample_lines()
        if not lines:
            return
        center = lines_intersection(self.sample_lines(), self.tol)
        if center is None:
            return
        pl = self.edge.findPlane(self.tol)
        if pl is None:
            return
        pt1 = self.edge.valueAt(self.fp)
        radius = pt1.distanceToPoint(center)
        circle = Part.Circle(center, pl.Axis, radius)
        return circle

    def fix_rotation(self, circle):
        mid_par = self.edge.Curve.parameterAtDistance(self.edge.Length / 2, self.fp)
        pt1 = self.edge.valueAt(mid_par)
        v1 = circle.Center - pt1
        v2 = self.edge.valueAt(self.fp) - circle.Center
        angle = -v1.getAngle(v2) * 180 / pi
        self.logger.debug(f"Rotating {angle:.2f}°")
        plm = FreeCAD.Placement()
        plm.rotate(circle.Center, circle.Axis, angle)
        circle.transform(plm.Matrix)
        par1 = circle.parameter(self.edge.valueAt(self.fp))
        par2 = circle.parameter(self.edge.valueAt(self.lp))
        if par1 > par2:
            par1, par2 = par2, par1
        return circle.toShape(par1, par2)

    def get_curve(self):
        circle = self.get_circle()
        if circle:
            self.logger.info("Curve is a circle")
            return self.fix_rotation(circle)
        line = self.get_line()
        if line:
            self.logger.info("Curve is a line")
            return line

"""
from importlib import reload
from freecad.Curves.lib import curve_identifier
reload(curve_identifier)
ci = curve_identifier.CurveIdentifier(e1, tol=1e-7)
new_edge = ci.get_curve()
print(ci.gap, ci.max_dev_param)
if new_edge:
    Part.show(new_edge)
"""
