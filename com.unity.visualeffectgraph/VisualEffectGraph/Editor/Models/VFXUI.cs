using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.VFX;
using UnityEngine.Serialization;

using Object = UnityEngine.Object;

namespace UnityEditor.VFX
{
    [Serializable]
    struct VFXNodeID
    {
        public VFXNodeID(VFXModel model, int id)
        {
            this.model = model;
            this.isStickyNote = false;
            this.id = id;
        }

        public VFXNodeID(int id)
        {
            this.model = null;
            this.isStickyNote = true;
            this.id = id;
        }

        public VFXModel model;
        public int id;

        public bool isStickyNote;
    }
    class VFXUI : ScriptableObject, IModifiable
    {
        [System.Serializable]
        public class UIInfo
        {
            public UIInfo()
            {
            }

            public UIInfo(UIInfo other)
            {
                title = other.title;
                position = other.position;
            }

            public string title;
            public Rect position;
        }

        [System.Serializable]
        public class GroupInfo : UIInfo
        {
            [FormerlySerializedAs("content")]
            public VFXNodeID[] contents;
            public GroupInfo()
            {
            }

            public GroupInfo(GroupInfo other) : base(other)
            {
                contents = other.contents;
            }
        }

        [System.Serializable]
        public class StickyNoteInfo : UIInfo
        {
            public string contents;
            public string theme;
            public string textSize;

            public StickyNoteInfo()
            {
            }

            public StickyNoteInfo(StickyNoteInfo other) : base(other)
            {
                contents = other.contents;
                theme = other.theme;
                textSize = other.textSize;
            }
        }

        public GroupInfo[] groupInfos;
        public StickyNoteInfo[] stickyNoteInfos;

        public Rect uiBounds;

        public Action<VFXModel> onModified;
        Action<VFXModel> IModifiable.onModified{get{return onModified;}set{onModified = value;}}

        internal void Sanitize(VFXGraph graph)
        {
            if (groupInfos != null)
                foreach (var groupInfo in groupInfos)
                {
                    //Check first, rebuild after because in most case the content will be valid, saving an allocation.
                    if (groupInfo.contents != null && groupInfo.contents.Any(t => (!t.isStickyNote || t.id >= stickyNoteInfos.Length) && !graph.children.Contains(t.model)))
                    {
                        groupInfo.contents = groupInfo.contents.Where(t => (t.isStickyNote && t.id < stickyNoteInfos.Length) || graph.children.Contains(t.model)).ToArray();
                    }
                }
        }
    }
}
