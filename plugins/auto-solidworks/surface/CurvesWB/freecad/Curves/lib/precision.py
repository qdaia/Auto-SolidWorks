import FreeCAD


tol3d = FreeCAD.Base.Precision.confusion()
tol2d = FreeCAD.Base.Precision.parametric(tol3d)
tolang = FreeCAD.Base.Precision.angular()
