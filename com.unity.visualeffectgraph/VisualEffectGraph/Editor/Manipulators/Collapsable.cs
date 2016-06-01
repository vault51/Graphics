using System;using System.Collections.Generic;using System.Reflection;using UnityEngine;using UnityEditor.Experimental;using UnityEditor.Experimental.Graph;using Object = UnityEngine.Object;namespace UnityEditor.Experimental{    internal abstract class Collapsable : IManipulate    {        public bool Enabled;        private bool m_DoubleClick;        private Rect m_Area;        public Collapsable(bool bEnabled, bool doubleClick, Rect area)        {            Enabled = bEnabled;            m_DoubleClick = doubleClick;            m_Area = area;        }        public bool GetCaps(ManipulatorCapability cap)        {            return false;        }        public void AttachTo(CanvasElement element)        {            if (m_DoubleClick)
            {
                element.DoubleClick += ManageDoubleClick;
            }            element.MouseDown += ManageMouseDown;        }        private bool ManageMouseDown(CanvasElement element, Event e, Canvas2D parent)        {            if (e.type == EventType.Used)                return false;            if (!Enabled)                return false;            Rect ActiveArea = m_Area;            ActiveArea.position += element.canvasBoundingRect.position;            if (ActiveArea.Contains(parent.MouseToCanvas(e.mousePosition)))            {                DoCollapse(element,e,parent);                parent.Layout();                element.parent.Invalidate();                e.Use();                return true;            }            return false;        }        private bool ManageDoubleClick(CanvasElement element, Event e, Canvas2D parent)        {            if (e.type == EventType.Used)                return false;            if (!Enabled)                return false;            DoCollapse(element,e,parent);            parent.Layout();            element.parent.Invalidate();            e.Use();            return true;        }        protected abstract void DoCollapse(CanvasElement element, Event e, Canvas2D parent);    };    internal class NodeBlockCollapse : Collapsable    {        public NodeBlockCollapse(bool bEnabled, bool bUseDoubleClick)            : base(bEnabled, bUseDoubleClick, VFXEditorMetrics.NodeBlockHeaderFoldoutRect)        {}        protected override void DoCollapse(CanvasElement element, Event e, Canvas2D parent)        {            element.parent.collapsed = !element.parent.collapsed;        }    }    internal class PropertySlotFieldCollapse : Collapsable    {        public PropertySlotFieldCollapse(Rect area)            : base(true, false, area)        {}        protected override void DoCollapse(CanvasElement element, Event e, Canvas2D parent)        {            var field = (VFXUIPropertySlotField)element;            if (!field.IsConnected())                field.ToggleCollapseChildren();        }    }}