from math import degrees
from FreeCAD import Vector
from Part import Cone, Circle, Line
from freecad.Curves.lib.logger import FCLogger
from freecad.Curves.lib.precision import tol3d


def build_cone(apex: Vector, circle: Circle) -> Cone:
    "Returns a cone from apex and circle"
    logger = FCLogger("Debug", "Cone Builder")
    dist = apex.distanceToLine(circle.Center, circle.Axis)
    if dist > tol3d:
        logger.error("Apex is not on circle axis")
        return
    cone = Cone()
    axis_line = Line(circle.Center, circle.Center + circle.Axis)
    apex_param = axis_line.parameter(apex)
    if abs(apex_param) < tol3d:
        logger.error("Cannot build flat cone")
        return
    if apex_param > 0:
        cone.Axis = -circle.Axis
    else:
        cone.Axis = circle.Axis
    cone.Center = circle.Center
    circle_start = circle.value(circle.FirstParameter)
    v1 = circle_start - apex
    v2 = circle.Center - apex
    angle = v1.getAngle(v2)
    cone.SemiAngle = angle
    cone.Radius = circle.Radius
    return cone

