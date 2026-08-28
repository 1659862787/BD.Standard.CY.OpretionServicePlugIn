using Kingdee.BOS.App.Data;
using Kingdee.BOS.Core.DynamicForm.PlugIn;
using Kingdee.BOS.Core.DynamicForm.PlugIn.Args;
using Kingdee.BOS.Orm.DataEntity;
using Kingdee.BOS.WebApi.FormService;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace BD.Standard.CY.OpretionServicePlugIn
{
    [Description("诚宇跨组织生成单据服务插件")]
    [Kingdee.BOS.Util.HotUpdate]
    public class AuditOperationPlugIn : AbstractOperationServicePlugIn
    {
        public override void OnPreparePropertys(PreparePropertysEventArgs e)
        {
            base.OnPreparePropertys(e);
            List<Kingdee.BOS.Core.Metadata.FieldElement.Field> fields = this.BusinessInfo.GetFieldList();
            foreach (var item in fields)
            {
                e.FieldKeys.Add(item.Key);
            }
        }

        /// <summary>
        /// 01.07.001组织的采购订单，下推审核了采购入库单之后。
        ///在01.07.002组织里，
        ///     由关联的一份01.07.002组织的销售订单，自动下推并审核01.07.002组织的销售出库单。
        ///     同时由关联的一份01.07.002组织采购订单，自动下推并审核01.07.002组织的采购入库单；
        ///其中，01.07.002组织的销售订单和采购订单，是由01.07.001组织的采购订单下推的（这个可以直接配置，不需要开发）
        /// </summary>
        /// <param name="e"></param>
        /// <exception cref="Exception"></exception>
        public override void EndOperationTransaction(EndOperationTransactionArgs e)
        {
            base.EndOperationTransaction(e);
            string Logpath = @"D:\BaoDieKFLog\AuditOperationPlugIn\" + DateTime.Now.ToString("yyyyMM");
            Logger logger = new Logger(Logpath, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");

            try
            {
                foreach (DynamicObject entity in e.DataEntitys)
                {
                    string id = entity[0].ToString();
                    string PurchaseOrgId = entity["PurchaseOrgId"].ToString();
                    StringBuilder stkinstock001 = new StringBuilder();
                    stkinstock001.AppendLine("select c.FSBILLID, c.FSID, b.FREALQTY");
                    stkinstock001.AppendLine("from T_STK_INSTOCK a");
                    stkinstock001.AppendLine("inner join T_STK_INSTOCKENTRY b on a.fid = b.FID");
                    stkinstock001.AppendLine("inner join T_STK_INSTOCKENTRY_lk c on c.fentryid = b.fentryid");
                    stkinstock001.AppendLine($"where a.fid = {id}");
                    stkinstock001.AppendLine("  and FSTABLENAME = 't_PUR_POOrderEntry'");
                    stkinstock001.AppendLine("  and FPURCHASEORGID = 202440");
                    stkinstock001.AppendLine("  and F_ZOCM_CheckBox_83g = 1");
                    stkinstock001.AppendLine("  and F_ZOCM_OrgId_k79 = 3097297");
                    DynamicObjectCollection dyob = DBUtils.ExecuteDynamicObject(this.Context, stkinstock001.ToString());
                    if (dyob.Count > 0)
                    {
                        string FSIDS = string.Join(",", dyob.Select(obj => obj["FSID"].ToString()));
                        logger.WriteLog("01.07.001组织采购入库单审核后，01.07.002组织销售出库单生成开始。原采购订单明细id集：" + FSIDS.ToString());

                        //获取01.07.002组织采购订单
                        CreatePurInStock(dyob, FSIDS, logger);

                        //获取01.07.002组织销售订单
                        CreateSalOutStock(dyob, FSIDS, logger);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.WriteLog("数据出现异常,错误信息：" + ex.Message);
                logger.WriteLog("             堆栈信息：" + ex.StackTrace);
                throw new Exception(ex.Message);
            }
        }



        private void CreatePurInStock(DynamicObjectCollection dyob, string FSIDS, Logger logger)
        {
            //获取01.07.002组织采购订单
            DynamicObjectCollection dynamicObjects = DBUtils.ExecuteDynamicObject(this.Context, $"select fentryid,F_ZOCM_Text_apv from T_PUR_POORDERENTRY where F_ZOCM_Text_apv IN ({FSIDS})");
            if (dynamicObjects.Count > 0)
            {
                Dictionary<string, string> dic = new Dictionary<string, string>();
                foreach (var item in dynamicObjects)
                {
                    string frealQty = dyob.FirstOrDefault(dyobItem => dyobItem["FSID"].ToString() == item["F_ZOCM_Text_apv"].ToString())?["FREALQTY"].ToString();
                    dic.Add(item["FENTRYID"].ToString(), frealQty);
                }
                string fentryIds = string.Join(",", dynamicObjects.Select(obj => obj["FENTRYID"].ToString()));
                logger.WriteLog("01.07.002组织收料生成开始。获取采购订单明细id集：" + fentryIds);
                JObject json = new JObject()
                {
                    { "EntryIds",fentryIds },
                    { "TargetFormId","PUR_ReceiveBill" },
                    { "IsEnableDefaultRule","true" },
                    { "IsDraftWhenSaveFail","true" }
                };

                string MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Push(Context, "PUR_PurchaseOrder", json.ToString()));
                if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                {
                    logger.WriteLog("01.07.002组织收料生成成功。");
                    string fid = ((JContainer)JObject.Parse(JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["SuccessEntitys"][0].ToString()).First).First.ToString();
                    JArray fDetailEntity = (JArray)JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["SuccessEntitys"][0]["EntryIds"]["FDetailEntity"];
                    string fentryids = string.Join(",", fDetailEntity);

                    //获取收料通知单明细id集，用来匹配数量
                    DynamicObjectCollection dynamicObjects1 = DBUtils.ExecuteDynamicObject(this.Context, $"select FPOORDERENTRYID,FENTRYID from  T_PUR_RECEIVEENTRY where FENTRYID in ({fentryids})");
                    logger.WriteLog("获取收料通知单id：" + fid + "，明细id集：" + fentryids);
                    JArray FDetailEntity = new JArray();
                    foreach (dynamic obj in dynamicObjects1)
                    {
                        string fentryid = obj["FENTRYID"].ToString();
                        string fsoentryid = obj["FPOORDERENTRYID"].ToString();
                        string frealQty = dic[fsoentryid];
                        JObject FDetailEntityItem = new JObject()
                                {
                                    new JProperty("FEntryID",fentryid),
                                    new JProperty("FStockQty",frealQty),
                                    new JProperty("FCheckInComing","false"),
                                    new JProperty("FStockID",new JObject(){new JProperty("FNumber", "CK018") }),
                                };
                        FDetailEntity.Add(FDetailEntityItem);
                    }
                    //保存json
                    JObject model = new JObject()
                            {
                                new JProperty("fid",fid),
                                //new JProperty("FSaleDeptID",new JObject(){new JProperty("FNumber", "03") }),
                                new JProperty("FDetailEntity",FDetailEntity),
                            };
                    JObject jsons = new JObject()
                            {
                                new JProperty("IsAutoSubmitAndAudit","false"),
                                new JProperty("model",model),
                            };

                    logger.WriteLog("保存json：" + jsons.ToString());
                    MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Save(Context, "PUR_ReceiveBill", jsons.ToString()));
                    if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                    {
                        //成功
                        logger.WriteLog("保存成功");
                        JObject json1 = new JObject()
                        {
                            { "Ids",fid },
                        };
                        MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Submit(Context, "PUR_ReceiveBill", json1.ToString()));
                        if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                        {
                            logger.WriteLog("提交成功");
                            MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Audit(Context, "PUR_ReceiveBill", json1.ToString()));
                            if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                            {
                                logger.WriteLog("审核成功");

                                //下推采购入库
                                JObject json2 = new JObject()
                                {
                                    { "Ids",fid },
                                    { "TargetFormId","STK_InStock" },
                                    { "IsEnableDefaultRule","true" },
                                    { "IsDraftWhenSaveFail","true" }
                                };
                                MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Push(Context, "PUR_ReceiveBill", json2.ToString()));
                                if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                                {
                                    string infid = ((JContainer)JObject.Parse(JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["SuccessEntitys"][0].ToString()).First).First.ToString();
                                    JObject json3 = new JObject()
                                    {
                                        { "Ids",infid },
                                    };
                                    MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Submit(Context, "STK_InStock", json3.ToString()));
                                    if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                                    {
                                        logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】采购入库单提交成功");
                                        MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Audit(Context, "STK_InStock", json3.ToString()));
                                        if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                                        {
                                            logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】采购入库单审核成功");
                                        }
                                        else
                                        {
                                            logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】采购入库单审核失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                                            throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】采购入库单审核失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                                        }
                                    }
                                    else
                                    {
                                        logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】采购入库单提交失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                                        throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】采购入库单提交失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                                    }
                                }
                                else
                                {
                                    logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】收料通知单下推采购入库单失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                                    throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】收料通知单下推采购入库单失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                                }
                            }
                            else
                            {
                                logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】收料通知单审核失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                                throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】收料通知单审核失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                            }
                        }
                        else
                        {
                            logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】收料通知单提交失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                            throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】收料通知单提交失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                        }
                    }
                    else
                    {
                        logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】收料通知单保存失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                        throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】收料通知单保存失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                    }
                }
                else
                {
                    throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】收料通知单处理失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                }

            }
        }


        /// <summary>
        /// 获取01.07.002组织销售订单
        /// </summary>
        /// <param name="dyob"></param>
        /// <param name="FSIDS"></param>
        /// <param name="logger"></param>
        private void CreateSalOutStock(DynamicObjectCollection dyob, string FSIDS, Logger logger)
        {
            DynamicObjectCollection dynamicObjects = DBUtils.ExecuteDynamicObject(this.Context, $"select fentryid,F_ZOCM_Text_apv from T_SAL_ORDERENTRY where F_ZOCM_Text_apv IN ({FSIDS})");
            if (dynamicObjects.Count > 0)
            {
                Dictionary<string, string> dic = new Dictionary<string, string>();
                foreach (var item in dynamicObjects)
                {
                    string frealQty = dyob.FirstOrDefault(dyobItem => dyobItem["FSID"].ToString() == item["F_ZOCM_Text_apv"].ToString())?["FREALQTY"].ToString();
                    dic.Add(item["FENTRYID"].ToString(), frealQty);
                }
                string fentryIds = string.Join(",", dynamicObjects.Select(obj => obj["FENTRYID"].ToString()));
                logger.WriteLog("01.07.002组织销售出库单生成开始。获取销售订单明细id集：" + fentryIds);
                JObject json = new JObject()
                {
                    { "EntryIds",fentryIds },
                    { "TargetFormId","SAL_OUTSTOCK" },
                    { "IsEnableDefaultRule","true" },
                    { "IsDraftWhenSaveFail","true" }
                };

                string MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Push(Context, "SAL_SaleOrder", json.ToString()));
                if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                {
                    logger.WriteLog("01.07.002组织销售出库单生成成功。");
                    string fid = ((JContainer)JObject.Parse(JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["SuccessEntitys"][0].ToString()).First).First.ToString(); ;



                    JArray FEntity = (JArray)JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["SuccessEntitys"][0]["EntryIds"]["FEntity"];
                    string fentryids = string.Join(",", FEntity);
                    DynamicObjectCollection dynamicObjects1 = DBUtils.ExecuteDynamicObject(this.Context, $"select FSOEntryId,FENTRYID from T_SAL_OUTSTOCKENTRY_R where FENTRYID in ({fentryids})");
                    logger.WriteLog("获取销售出库单id：" + fid+"，明细id集：" + fentryids);
                    JArray FEntitys = new JArray();
                    foreach (dynamic obj in dynamicObjects1)
                    {
                        string fentryid = obj["FENTRYID"].ToString();
                        string fsoentryid = obj["FSOEntryId"].ToString();
                        string frealQty = dic[fsoentryid];
                        JObject FEntityItem = new JObject()
                                {
                                    new JProperty("FEntryID",fentryid),
                                    new JProperty("FRealQty",frealQty),
                                    new JProperty("FStockID",new JObject(){new JProperty("FNumber", "CK018") }),
                                };
                        FEntitys.Add(FEntityItem);
                    }
                    //保存json
                    JObject model = new JObject()
                            {
                                new JProperty("fid",fid),
                                //new JProperty("FSaleDeptID",new JObject(){new JProperty("FNumber", "03") }),
                                new JProperty("FEntity",FEntitys),
                            };
                    JObject jsons = new JObject()
                            {
                                new JProperty("IsAutoSubmitAndAudit","false"),
                                new JProperty("model",model),
                            };

                    logger.WriteLog("保存json：" + jsons.ToString());
                    MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Save(Context, "SAL_OutStock", jsons.ToString()));
                    if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                    {
                        //成功
                        logger.WriteLog("保存成功");
                        JObject json1 = new JObject()
                        {
                            { "Ids",fid },
                        };
                        MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Submit(Context, "SAL_OutStock", json1.ToString()));
                        if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                        {
                            logger.WriteLog("提交成功");
                            MessageReturned = JsonConvert.SerializeObject(WebApiServiceCall.Audit(Context, "SAL_OutStock", json1.ToString()));
                            if (JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["IsSuccess"].ToString().Equals("True"))
                            {
                                logger.WriteLog("审核成功");
                            }
                            else
                            {
                                logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】销售出库单审核失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                                throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】销售出库单审核失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                            }
                        }
                        else
                        {
                            logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】销售出库单提交失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                            throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】销售出库单提交失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                        }
                    }
                    else
                    {
                        logger.WriteLog("【01.07.002安徽花洁尔生活科技有限公司】销售出库单保存失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                        throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】销售出库单保存失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                    }
                }
                else
                {
                    throw new Exception("【01.07.002安徽花洁尔生活科技有限公司】销售出库单处理失败，错误信息：" + JObject.Parse(MessageReturned)["Result"]["ResponseStatus"]["Errors"][0]["Message"].ToString());
                }

            }
        }


    }
}
