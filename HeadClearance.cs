using Ingr.Smart3D.Automation.DesignRuleChecker;
using Ingr.SP3D.Common.Client;
using Ingr.SP3D.Common.Client.Services;
using Ingr.SP3D.Common.Middle;
using Ingr.SP3D.Common.Middle.Services;
using Ingr.SP3D.Common.Middle.Services.Hidden;
using Ingr.SP3D.Equipment.Middle;
using Ingr.SP3D.Structure.Middle;
using Ingr.SP3D.Interop.ModelGeometry;
using RuleCheckerUtilities;
using SECLDRCUtility;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace SECLDesignRule02
{
    public class HeadClearance
        : DRCBaseRule
    {
        
        const string PARTCLASSES = "partclasses";
        const string TAG = "tag";
        const string RANGE = "range";
        double allowanceXY = 0.02;
        double allowanceZ = 0.001;
        static string _mainObjsSQL, _refObjSQL;         // contains a complete select query statement for main or reference object
        static bool _ruleDataInitialized = false;                               // flag to identify if configuration data has bC:\Users\cadman\Desktop\DRC\SECLDesignRule02\SECLDesignRule02\HeadClearance.cseen read at least once
        //static double _dMaxClearance = -999.0;          // contains the maximum clearance value in this rule config file
        static RuleDescriptionHelper _oRuleDescriptionHelper = null;            // helps you get rule error descriptions easily.
        private HeadClearanceCheck _oSetting;
         

        public override bool CheckCustomRule(ReadOnlyCollection<BusinessObject> oBOCObjects, Ingr.SP3D.Common.Middle.FilterBase oUniqueCheckFilter)
        {
            _ruleDataInitialized = false;
            if (!GetRuleSpecificConfigData()) return false;             //ConfigData Check
            ProcessDeletedObjects(oUniqueCheckFilter);
            ProcessMainObjects(oBOCObjects, oUniqueCheckFilter);
            return false;
        }
        private void ProcessDeletedObjects(FilterBase oUniqueCheckFilter)
        {
            //Rule will get invoked with a scheduled CmdLineTaskRunner updating a special object named "DeletedReferenceObjectTracker"
            var ruleRefObjOIDs = GetOIDsOfRuleReferenceObjectsNotYetCleanedUp(RuleID);
            if (ruleRefObjOIDs.GetLength(0) == 0) return;

            var rroDeletedOIDsToProcess = GenericUtils.GetDeletedObjectOIDsInModelDBCoreBaseClassTable(ruleRefObjOIDs, "OIDsOfRuleReferenceObjects", "");
            foreach (string rroDeletedOID in rroDeletedOIDsToProcess)
            {
                Log("Processing Deleted object:" + rroDeletedOID);
                try
                {
                    MarkRuleReferenceObjectAsCleanedup(RuleID, rroDeletedOID);
                }
                catch (Exception ex)
                {
                    Log("Error occurred: " + rroDeletedOID, ex);
                }
            }
        }
        private void ProcessMainObjects(ReadOnlyCollection<BusinessObject> oBOCObjects, FilterBase oUniqueCheckFilter)
        {
            foreach (var oObj in oBOCObjects)
            {
                foreach (var MainObject in _oSetting.InfoForReferenceObjectRules.MainObjectsInterface.Split('|').Select(x => x.Trim())) //Mainobject in XML
                {
                    if (!oObj.SupportsInterface(MainObject.ToString()))         //If not contains, Interface of Mainobject (Stair or Slab)
                        continue;
                    if (oObj is Stair && oObj.GetPropertyValue("IJNamedItem", "Name").ToString().Contains("Stair"))     //Stair
                    {
                        var oStair = oObj as Stair;                             //Stair Object
                        Matrix4X4 oMat = new Matrix4X4(oStair.Matrix);          // StairMatrix (Global Axis -> Local Axis)
                        Vector rotateVec = oMat.XAxis;                          //X Axis of StairMatrix : Rotate base on X Axis
                        IMathServices oMathService = new Math3d();
                        Collection<BusinessObject> oColl = new Collection<BusinessObject>() { oStair };
                        
                        var oRange = (oStair as IRange).Range;
                        double dRadian = (oStair.GetPropertyValue("IJSPSCommonStairLadderProps", "Angle") as PropertyValueDouble).PropValue.Value;   //Stair Angle
                        double height = MiddleServiceProvider.UOMMgr.ParseUnit(UnitType.Distance, _oSetting.Clearance.Value);    //Height of user set
                        Position posSrc, posSrc2;
                        double dMinDist;
                        
                        oMat.Rotate(dRadian, rotateVec);                        //Based on X Axis of Stair, Matrix will rotate by Angle
                        OrientedRangeBox oOrientedRangeBox = oMathService.GetOrientedRangeBox(oColl, oMat.XAxis, oMat.YAxis);        //Oriented Range Box of Stair

                        //OriginXYZ <-> Y Axis Near surface minDistance and Position on surface
                        oRange.GetFaceInGivenDirection(oMat.YAxis, true).DistanceBetween(new Point3d(oOrientedRangeBox.Origin.X,
                                                                                                     oOrientedRangeBox.Origin.Y,
                                                                                                     oOrientedRangeBox.Origin.Z), out dMinDist, out posSrc);
                        //OriginXYZ <-> Y Axis Far surface minDIstance and Position on surface
                        oRange.GetFaceInGivenDirection(oMat.YAxis, false).DistanceBetween(new Point3d(oOrientedRangeBox.Origin.X,
                                                                                                     oOrientedRangeBox.Origin.Y,
                                                                                                     oOrientedRangeBox.Origin.Z), out dMinDist, out posSrc2);

                        double dJustMinDist = posSrc.DistanceToPoint(posSrc2);      //posSrc <-> posSrc2 Distance
                        double c = dJustMinDist / Math.Cos(dRadian);                //Distance / cos  = length of sloped stair
                        Vector oTanVector = new Vector(oOrientedRangeBox.Sides[1]);  //Tangent Vector of Oriented Range Box (Y Axis)
                        oTanVector.Length = c;
                        Position corner1 = new Position(posSrc);
                        Position corner2 = new Position(posSrc).Offset(oOrientedRangeBox.Sides[0]);
                        Position corner3 = new Position(posSrc).Offset(oOrientedRangeBox.Sides[0]).Offset(oTanVector);
                        Position corner4 = new Position(posSrc.Offset(oTanVector));
                        //Corners of stair

                        double dPostHeight = MiddleServiceProvider.UOMMgr.ParseUnit(UnitType.Distance, oStair.GetPropertyValue("IJUAStairTypeA", "PostHeight").ToString());
                        double dTopElev = Math.Max(Math.Max(corner1.Z, corner2.Z),
                                              Math.Max(corner3.Z, corner4.Z));      //Maximum elevation of bottom of stair
                        double dHeight = (oRange.High.Z - dTopElev);                // Top point of Handrail - bottom Elevation of Stair = Stair Height
                        corner1.Z += dHeight; corner2.Z += dHeight; corner3.Z += dHeight; corner4.Z += dHeight;     // + Stair Height

                        Vector uVec = corner2.Subtract(corner1);                    //UVector of Symbol
                        Vector vVec = corner3.Subtract(corner2);                    //Vvector of Symbol
                        //uVec.Length=1.0; vVec.Length=1.0;
                        OrientedRangeBox oOrientedRBHC = new OrientedRangeBox(new Position(corner1),           // Creates Oritented RangeBox of Head Clearance
                                                                              uVec, vVec, new Vector(0, 0, height - dHeight));     //2.0m - Stair height = RBHC RangeBox
                        RangeBox oExtendedRange = new RangeBox(new Position(oRange.Low),
                                                                               new Position(oRange.High.X, oRange.High.Y, oRange.High.Z + height));        // 2.0m is height value adjustable
                        
                        string subSelectQueryCriteria = GenericUtils.GetSQLQueryBasedOnObjectTypeViewCriteria("JDObject");
                        string sqlQuery = GenericUtils.GetSQLQueryForObjectsOverlappingRangeBox(oExtendedRange, subSelectQueryCriteria);
                        SQLFilter oFilter = new SQLFilter();
                        oFilter.SetSQLFilterString(sqlQuery);
                        ReadOnlyCollection<BusinessObject> oReadOnlyColl = oFilter.Apply(MiddleServiceProvider.SiteMgr.ActiveSite.ActivePlant.PlantModel);
                        //SQLFilter for Searching Overlapping Object

                        //Highlight object
                        GraphicViewHiliter oHiliter = new GraphicViewHiliter();                        
                        oHiliter.Weight = 5;
                        oHiliter.Color = ColorConstants.RGBYellow;
                        oHiliter.LinePattern = HiliterBase.HiliterLinePattern.Dotted;
                        Hiliter oHiliter2 = new Hiliter();
                        oHiliter2.Weight = 10;
                        oHiliter2.Color = ColorConstants.RGBBlue;
                        Hiliter oHiliter3 = new Hiliter();
                        oHiliter3.Weight = 3;
                        oHiliter3.Color = ColorConstants.RGBWhite;
                        Dictionary<string, List<BusinessObject>> oDic = new Dictionary<string, List<BusinessObject>>();
                        oDic["Overlap"] = new List<BusinessObject>();
                        oDic["Inside"] = new List<BusinessObject>();
                        ClientServiceProvider.SelectSet.SelectedObjects.Clear();

                        List<BusinessObject> refObjList = new List<BusinessObject>(); //BusinessObject List
                        List<String> stringList = new List<String>();                 //oid LIst

                        foreach (var oObject in oReadOnlyColl)
                        {
                            if (!IsSupportInerface(oObject))
                            {
                                refObjList.Add(oObject);
                                stringList.Add(oObject.ObjectID);
                            }
                            else
                            {
                                continue;
                            }

                            OrientedRangeBox oObjectOrientedBox = oMathService.GetOrientedRangeBox(new Collection<BusinessObject>(
                                new List<BusinessObject>() { oObject }));                                               //OrientedRangeBox of the Object
                            RangeBoxIntersectionType intersectionType = oOrientedRBHC.Intersects(oObjectOrientedBox);     //Find Intersects b/w them
                            if (intersectionType == RangeBoxIntersectionType.Overlap)
                            {
                                //ClientServiceProvider.SelectSet.SelectedObjects.Add(oObject);
                                oDic["Overlap"].Add(oObject);
                                //foreach (var oPlaTest in MakePlane3D6ByOrientedRangeBox(oPipeOrientedBox))
                                //{
                                //    oHiliter3.HilitedObjects.Add(oPlaTest);
                                //}

                            }
                            else if (intersectionType == RangeBoxIntersectionType.Inside)
                            {
                                //ClientServiceProvider.SelectSet.SelectedObjects.Add(oObject);
                                oDic["Inside"].Add(oObject);
                                //foreach (var oPlaTest in MakePlane3D6ByOrientedRangeBox(oPipeOrientedBox))
                                //{
                                //    oHiliter2.HilitedObjects.Add(oPlaTest);
                                //}
                            }
                            else
                            {
                                refObjList.Remove(oObject);
                            }
                            foreach (KeyValuePair<string, List<BusinessObject>> item in oDic)
                            {
                                if (item.Key.Equals("Overlap"))
                                {
                                    foreach (var oBO in oDic[item.Key])
                                    {
                                        oHiliter3.HilitedObjects.Add(oBO);
                                    }
                                }
                                else if (item.Key.Equals("Inside"))
                                {
                                    foreach (var oBO in oDic[item.Key])
                                    {
                                        oHiliter2.HilitedObjects.Add(oBO);
                                    }
                                }
                            }
                            //Debug.Print(oObj.ObjectIDForQuery);
                        }
                        var oPlaColl = MakePlane3D6ByOrientedRangeBox(oOrientedRangeBox);
                        var oPlaColl2 = MakePlane3D6ByOrientedRangeBox(oOrientedRBHC);

                        foreach (var oPlane in oPlaColl2)
                            oHiliter.HilitedObjects.Add(oPlane);

                        if (refObjList.Count == 0)
                        {
                            CreateOrUpdateDefect(oObj, 1, false, "", null, null);
                        }
                        else
                        {
                            Dictionary<string, string> DefectDic = new Dictionary<string, string>() { { "distance", _oSetting.Clearance.Value } };
                            CreateOrUpdateDefect(oObj, 1, true, _oRuleDescriptionHelper.GetFullErrorDescription(1, DefectDic), refObjList, null);
                        }
                        
                    }
                    else if (oObj is Slab && oObj.GetPropertyValue("IJNamedItem", "Name").ToString().Contains("Stair"))
                    {
                        IMathServices oMathService = new Math3d();
                        var oSlab = oObj as Slab;
                        //Matrix4X4 oMat = new Matrix4X4(oSlab.Matrix);
                        Collection<BusinessObject> oCollection = new Collection<BusinessObject>() { oSlab };

                        // RangeBox 를 가져온다.
                        RangeBox oRange = (oSlab as IRange).Range;
                        // Calculate the distance value based on the unit recored on the configuration file
                        double height = MiddleServiceProvider.UOMMgr.ParseUnit(UnitType.Distance, _oSetting.Clearance.Value);
                        // 면적을 줄여준다. (20 mm)
                        RangeBox nRange = new RangeBox(new Position(oRange.Low.X + allowanceXY, oRange.Low.Y + allowanceXY, oRange.High.Z + allowanceZ),
                                                   new Position(oRange.High.X - allowanceXY, oRange.High.Y - allowanceXY, oRange.High.Z + height));
                        RangeBox testRange = new RangeBox();
                        OrientedRangeBox oOrientedRangeBox = oMathService.GetOrientedRangeBox(oCollection);
                        oOrientedRangeBox.GetExtendedOrientedRangeBoxInGivenDirection(new Vector (0,0,1), height, out oOrientedRangeBox, out testRange);

                        //JDObject(모든오브젝트)에 대한 Query 생성
                        string subQuery = GenericUtils.GetSQLQueryBasedOnObjectTypeViewCriteria("JDObject");
                        string fullQuery = GenericUtils.GetSQLQueryForObjectsOverlappingRangeBox(nRange, subQuery);
                        //Excutes Query
                        SQLFilter oFilter = new SQLFilter();
                        oFilter.SetSQLFilterString(fullQuery);
                        var oColl = oFilter.Apply();                                  //Query에 걸리는 Object Collection
                        List<BusinessObject> refObjList = new List<BusinessObject>(); //BusinessObject List
                        List<String> stringList = new List<String>();

                        //Highlight object
                        GraphicViewHiliter oHiliter = new GraphicViewHiliter();
                        oHiliter.Weight = 5;
                        oHiliter.Color = ColorConstants.RGBYellow;
                        oHiliter.LinePattern = HiliterBase.HiliterLinePattern.Dotted;
                        Hiliter oHiliter2 = new Hiliter();
                        oHiliter2.Weight = 10;
                        oHiliter2.Color = ColorConstants.RGBBlue;
                        Hiliter oHiliter3 = new Hiliter();
                        oHiliter3.Weight = 3;
                        oHiliter3.Color = ColorConstants.RGBWhite;
                        Dictionary<string, List<BusinessObject>> oDic = new Dictionary<string, List<BusinessObject>>();
                        oDic["Overlap"] = new List<BusinessObject>();
                        oDic["Inside"] = new List<BusinessObject>();
                        ClientServiceProvider.SelectSet.SelectedObjects.Clear();

                        foreach (var oObject in oColl)
                        {
                            if (!IsSupportInerface(oObject))
                            {
                                refObjList.Add(oObject);
                                stringList.Add(oObject.ObjectID);
                            }
                            else
                            {
                                continue;
                            }
                            
                            OrientedRangeBox oObjectOrientedBox = oMathService.GetOrientedRangeBox(new Collection<BusinessObject>(
                                new List<BusinessObject>() { oObject }));                                               //OrientedRangeBox of the Object
                            RangeBoxIntersectionType intersectionType = oOrientedRangeBox.Intersects(oObjectOrientedBox);     //Find Intersect b/w them
                            if (intersectionType == RangeBoxIntersectionType.Overlap)
                            {
                                //ClientServiceProvider.SelectSet.SelectedObjects.Add(oObject);
                                oDic["Overlap"].Add(oObject);
                                //foreach (var oPlaTest in MakePlane3D6ByOrientedRangeBox(oPipeOrientedBox))
                                //{
                                //    oHiliter3.HilitedObjects.Add(oPlaTest);
                                //}

                            }
                            else if (intersectionType == RangeBoxIntersectionType.Inside)
                            {
                                //ClientServiceProvider.SelectSet.SelectedObjects.Add(oObject);
                                oDic["Inside"].Add(oObject);
                                //foreach (var oPlaTest in MakePlane3D6ByOrientedRangeBox(oPipeOrientedBox))
                                //{
                                //    oHiliter2.HilitedObjects.Add(oPlaTest);
                                //}
                            }
                            foreach (KeyValuePair<string, List<BusinessObject>> item in oDic)
                            {
                                if (item.Key.Equals("Overlap"))
                                {
                                    foreach (var oBO in oDic[item.Key])
                                    {
                                        oHiliter3.HilitedObjects.Add(oBO);
                                    }
                                }
                                else if (item.Key.Equals("Inside"))
                                {
                                    foreach (var oBO in oDic[item.Key])
                                    {
                                        oHiliter2.HilitedObjects.Add(oBO);
                                    }
                                }
                            }
                            //Debug.Print(oObj.ObjectIDForQuery);
                        }
                        //var oPlaColl = MakePlane3D6ByOrientedRangeBox(oOrientedRangeBox);
                        var oPlaColl2 = MakePlane3D6ByOrientedRangeBox(oOrientedRangeBox);

                        foreach (var oPlane in oPlaColl2)
                            oHiliter.HilitedObjects.Add(oPlane);

                        if (refObjList.Count == 0)
                        {
                            CreateOrUpdateDefect(oObj, 1, false, "", null, null);
                        }
                        else
                        {
                            Dictionary<string, string> DefectDic = new Dictionary<string, string>() { { "distance", _oSetting.Clearance.Value } };
                            CreateOrUpdateDefect(oObj, 1, true, _oRuleDescriptionHelper.GetFullErrorDescription(1, DefectDic), refObjList, null);
                        }
                    }
                }
            }
        }

        /// <summary>
        ///             Make all plane3Ds from the oriented range box.
        /// </summary>
        /// <param name="oBox">Oriented RangeBox</param>
        /// <returns>List of Plane3D</returns>
        private List<Plane3d> MakePlane3D6ByOrientedRangeBox(OrientedRangeBox oBox)
        {
            Collection<Position> colPts1 = new Collection<Position>() 
            { 
                new Position(oBox.Origin),
                new Position(oBox.Origin.Offset(oBox.Sides[0])),
                new Position(oBox.Origin.Offset(oBox.Sides[0]).Offset(oBox.Sides[1])),
                new Position(oBox.Origin.Offset(oBox.Sides[1])) };
            Collection<Position> colPts2 = new Collection<Position>()
            {
                new Position(oBox.Origin.Offset(oBox.Sides[2])),
                new Position(oBox.Origin.Offset(oBox.Sides[2]).Offset(oBox.Sides[0])),
                new Position(oBox.Origin.Offset(oBox.Sides[2]).Offset(oBox.Sides[0]).Offset(oBox.Sides[1])),
                new Position(oBox.Origin.Offset(oBox.Sides[2]).Offset(oBox.Sides[1]))
            };
            Collection<Position> colPts3 = new Collection<Position>()
            {
                new Position(oBox.Origin),
                new Position(oBox.Origin.Offset(oBox.Sides[2])),
                new Position(oBox.Origin.Offset(oBox.Sides[2]).Offset(oBox.Sides[1])),
                new Position(oBox.Origin.Offset(oBox.Sides[1]))
            };
            Collection<Position> colPts4 = new Collection<Position>()
            {
                new Position(oBox.Origin),
                new Position(oBox.Origin.Offset(oBox.Sides[0])),
                new Position(oBox.Origin.Offset(oBox.Sides[0]).Offset(oBox.Sides[2])),
                new Position(oBox.Origin.Offset(oBox.Sides[2]))
            };
            Collection<Position> colPts5 = new Collection<Position>()
            {
                new Position(oBox.Origin.Offset(oBox.Sides[0])),
                new Position(oBox.Origin.Offset(oBox.Sides[0]).Offset(oBox.Sides[1])),
                new Position(oBox.Origin.Offset(oBox.Sides[0]).Offset(oBox.Sides[1]).Offset(oBox.Sides[2])),
                new Position(oBox.Origin.Offset(oBox.Sides[0]).Offset(oBox.Sides[2]))
            };
            Collection<Position> colPts6 = new Collection<Position>()
            {
                new Position(oBox.Origin.Offset(oBox.Sides[1])),
                new Position(oBox.Origin.Offset(oBox.Sides[1]).Offset(oBox.Sides[0])),
                new Position(oBox.Origin.Offset(oBox.Sides[1]).Offset(oBox.Sides[0]).Offset(oBox.Sides[2])),
                new Position(oBox.Origin.Offset(oBox.Sides[1]).Offset(oBox.Sides[2]))
            };

            Plane3d pla1 = new Plane3d(colPts1);
            Plane3d pla2 = new Plane3d(colPts2);
            Plane3d pla3 = new Plane3d(colPts3);
            Plane3d pla4 = new Plane3d(colPts4);
            Plane3d pla5 = new Plane3d(colPts5);
            Plane3d pla6 = new Plane3d(colPts6);

            return new List<Plane3d>() { pla1, pla2, pla3, pla4, pla5, pla6 };
        }

        private bool IsSupportInerface(BusinessObject oBO)
        {
            foreach (string objectinterface in _oSetting.Exception.ObjectsInterface.Split('|').Select(x => x.Trim()))
            {
                if (oBO.SupportsInterface(objectinterface) || oBO is TopologyPort)
                {
                    return true;
                }
            }
            return false;
        }

        private bool GetRuleSpecificConfigData()
        {
            // If you have read the configuration data once, exit from this method.
            if (_ruleDataInitialized) return true;         

            // Ready to read the rule configuration data for this rule
            XmlSerializer serializer = new XmlSerializer(typeof(HeadClearanceCheck));

            // Initialize RuleDescriptionHelper with RuleName
            _oRuleDescriptionHelper = new RuleDescriptionHelper(RuleName);

            try
            {
                // ConfigFiles(0) contains this rule configuration file path
                using (Stream reader = new FileStream(ConfigFiles(0), FileMode.Open))
                {
                    _oSetting = (HeadClearanceCheck)serializer.Deserialize(reader);

                    // Get maximum clearance value in configuration data 
                    // this value will be used to find equipments near the main equipment
                    
                    // Calculate the distance value based on the unit recored on the configuration file
                      
                        //// Get the maximum clearance value
                        //if (value > _dMaxClearance)
                        //    _dMaxClearance = value;
                    
                    string mainObjectSQLCriteria = "";
                    string referenceObjectSQLCriteria = "";

                    // Get main and reference object view name from the configuration file
                    mainObjectSQLCriteria = _oSetting.InfoForReferenceObjectRules.MainObjectsInterface;
                    referenceObjectSQLCriteria = _oSetting.InfoForReferenceObjectRules.ReferenceObjects;

                    // Create a complete select query statement for main or reference object
                    _mainObjsSQL = GenericUtils.GetSQLQueryBasedOnObjectTypeViewCriteria(mainObjectSQLCriteria);
                    _refObjSQL = GenericUtils.GetSQLQueryBasedOnObjectTypeViewCriteria(referenceObjectSQLCriteria);
                }
            }
            catch (Exception e)
            {
                Log("Error Reading Rule Configuration File - " + ConfigFiles(0), e);
                throw;
            }

            return (_ruleDataInitialized = true);
        }  
    }
        
    public class HeadClearanceDataClass
    {
        double _value = 0.0;
        public double Value
        {
            get { return _value; }
            set { _value = value; }
        }
        public HeadClearanceDataClass(double value)
        {
            // TODO: Complete member initialization
            _value = value;
        }
    }
    /// <remarks/>
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    [System.Xml.Serialization.XmlRootAttribute(Namespace = "", IsNullable = false)]
    public partial class HeadClearanceCheck
    {
        private HeadClearanceCheckRule clearanceField;
        private HeadClearanceCheckException exceptionField;
        private HeadClearanceCheckInfoForReferenceObjectRules InfoForReferenceObjectRulesField;

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute("Clearance")]
        public HeadClearanceCheckRule Clearance
        {
            get
            {
                return this.clearanceField;
            }
            set
            {
                this.clearanceField = value;
            }
        }
        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute("Exception")]
        public HeadClearanceCheckException Exception
        {
            get
            {
                return this.exceptionField;
            }
            set
            {
                this.exceptionField = value;
            }
        }
        /// <remarks/>
        public HeadClearanceCheckInfoForReferenceObjectRules InfoForReferenceObjectRules
        {
            get
            {
                return this.InfoForReferenceObjectRulesField;
            }
            set
            {
                this.InfoForReferenceObjectRulesField = value;
            }
        }
    }
    /// <remarks/>
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public partial class HeadClearanceCheckRule
    {
        private string valueField;
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string Value
        {
            get
            {
                return this.valueField;
            }
            set
            {
                this.valueField = value;
            }
        }
    }
        /// <remarks/>
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public partial class HeadClearanceCheckException
    {
        private string ObjectsInterfaceField;
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string ObjectsInterface
        {
            get
            {
                return this.ObjectsInterfaceField;
            }
            set
            {
                this.ObjectsInterfaceField = value;
            }
        }
    }
    /// <remarks/>
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public partial class HeadClearanceCheckInfoForReferenceObjectRules
    {
        private string mainObjectsField;

        private string referenceObjectsField;

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string MainObjectsInterface
        {
            get
            {
                return this.mainObjectsField;
            }
            set
            {
                this.mainObjectsField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string ReferenceObjects
        {
            get
            {
                return this.referenceObjectsField;
            }
            set
            {
                this.referenceObjectsField = value;
            }
        }
    }

}
